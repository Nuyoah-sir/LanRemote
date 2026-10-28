# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-10-28（最新完成片§19.73/ADR117：自动画质遥测接缝与决策核心已验证，代码4fd4099已普通push并核remote一致，9files/+450/-1；1新增生产+3生产改+3新增test）。同日先完成§19.72/ADR116 真实Control ping/pong与按会话RTT 9fb249f/7010；此前完成§19.71/ADR115 ping/pong帧与往返时延计量内核 0ffc111/7007、§19.70/ADR114 命名画质预设 ebd5e3d/6984与§19.69/ADR113 实际帧率指标 dcd5eea/6965。此前回填：§19.66/ADR110 产品侧切屏接线 93d72e2/6940、§19.67/ADR111 每会话画质底座 32420e4/6951、§19.68/ADR112 每会话画质接线 e250ca2/6961，四片均已push并核remote完整号一致；再前 §19.65/ADR109 内部显式多屏GDI采集 1028888/6922。新增53（45合成native+8真实offscreen），三轮定向各231PASS，四正式变异3/21/1/1原Assert红恢复；final-v2四Rebuild零警告错误、双配置各6922PASS，独立audit-v2退出0。旧Transport测试仅补54字节派发等待，原断言/身份保留，Capture生产和测试冻结。默认App仍旧主屏路径，offscreen非真实副屏/混合DPI/热插拔或双机验收；下一片接App Capture/Sessions友元、Host每会话prepare/writer、Client启用及窗口请求。冻结settings，不重验M4/M5、不启M7、不改网/防护/权限/旧ZIP。）**
>
> **M4历史两机进展：用户 A client `31255177` / B host `d8fd8b0d` 双方 PASS。** 成功会话按完整 SessionId 配对：serverProof已验证、Control授权、保持采样达标、自然注销；两个超时负例按端口精确配对，pin拒绝仅顺序关联。审批超时不再是M4阻断，**不外推到当前M5产品实屏**。
> **软件及验收不得导致任一端断网，短暂也不接受；不能以确认框/UAC或可撤销为理由继续改现网。新版已移除改网执行链，旧脚本也无条件拒绝；不默认执行 Undo。日志不能代替双方互联网无影响的人工确认。**
>
> **当前状态：M4完成。** 原Control/降权/拒绝/错key、联网/审批/短码、密钥显示与遮挡、监听关窗均已通过；`b42a1553`坏proof和`0cd5d3b8`人工活动停止日志通过，用户本轮明确确认固定文案完整可见、停止后恢复及原控件可访问。原FAIL/INVALID_RUN/UNMET结果保留，不翻绿、不重跑、不外推全DPI或内存擦除。
> **最近已提交代码：`1028888`，2026-10-21已普通push并核remote完整号`1028888b768b351384e239afd1d6f86b0a4efad5`一致。** 这是§19.65内部显式多屏GDI采集，5files/+1389/-19（2production改+2新test+1旧test单同步行），双配置各6922PASS；默认primary API/原路径保留，App仍未启用内部多屏，offscreen不是实际副屏验收。§19.64内部真实视频/JPEG/STA Image.Source逻辑提交屏障7757298/6869为历史，LastPresented非物理scanout；未新增public API，不是双机验收。§19.63 Client真实请求与严格回执关联842a53f/6847为历史；§19.62显式自动协调及真实首发回执b4055c6/6812为历史；§19.61请求读取4bf8458/6780为历史；§19.60写者7968725/6729为历史；§19.59Control回执/占槽d99a7e0/6692为历史；§19.58请求代际首发结算e8969f3/6154为历史；§19.57本地整帧发送边界c730476/6103为历史；§19.56内部切屏请求执行生命周期35a372d/6065为历史；§19.55 Control切屏请求纯合同/单槽准入580fc12/5999为历史；§19.54 Router内部可选换代生命周期029032b/5651为历史；§19.53发布接缝62afdf0/5615为历史；§19.52 internal拥有型视频换代来源6363e53/5584为历史；§19.51 internal可选视频发送编号cd2bfd0/5536为历史；§19.50 internal固定目标生产者准备f33277e/5475为历史；§19.49 Client公开只读目录与App显示/清理3a4280d/5434为历史；§19.48公开惰性来源与App默认GDI装配c745ae4/5410为历史；§19.47认证后目录生命周期8001488/5383为内部可选接线历史；§19.46 Router监督e7d098b/5361、§19.45 Host底座637e84e/5344历史保留；前片§19.44 Client目录接收79a532c/5279保留；§19.43完整V2-only接线33aca8f/5246是前一片；此前§19.42的1d917c8/5143和累计81文件检查点acec599保留，不伪造过去逐片提交。`b3e1273`为§19.22历史基线。M5此前十分钟、慢网、双方上网逐项用户确认保留；2026-10-12用户又明确稳定并要求告一段落，现按用户确认收口，不再把其作为M6阻断，不虚构原始数字/截图时间序列或独立性能审计。§19.29原ZIP不覆盖、不重打；M4不重做、键鼠M7不启动。
> 阶段 5 新增 8 项运行期变异 kill 并恢复；证据与局限见 §18.11，现行合同 ADR-042/043/044。
> **2026-09-24 用户已明确确认本轮A/B测试前、中、后上网始终正常，无观察到短暂中断；审批条目/按钮显示正常且批准前已核对两端短码一致。** 该Control轮网络无影响及审批交互人工项通过，不自动外推后续轮次或全部DPI、密钥清空、真实serverProof错误显示与关窗路径；降权和有效拒绝后续均已通过，见§18.16–17；其余原范围人工项现已配齐，最终收口见§18.25。
> **推进规则（2026-09-28用户重申）：不涉及人工辅助的测试由助手自主执行，通过后直接继续下一任务，不等待用户再次说“继续”。只有真正需要人工操作/观察或权限的依赖才暂停；不额外增设人工门槛，不重复已通过项，不把未验项目记为通过。** 原有里程碑DoD与安全约束不自动豁免；不再等待外部模型或重问已定交互。
> 当前**已归档于acec599的产品工作树**已接入App Host显式ViewOnly启停与WPF本机审批、Client远端密钥输入及Control/Video双TLS、JPEG有界WPF显示和退出排空；同机loopback夹具的冻结WPF画面已验证，**不能等同两台真实LAN产品GUI实屏**，仍无键鼠控制。新增修正要求已有配置的`allowDiscovery`必须存在且为唯一布尔值，真正首装无文件才允许只公告身份。App/Transport/Discovery等`src`及`tests`已随累计检查点`acec599`提交，不将前片`b3e1273`或文档提交误作当前App代码提交；M5候选已于§19.29出包，但不包含§19.30的M6本机目录。
> **M5已按用户确认收口；M6仍进行中。** §19.44唯一读者、§19.47认证后生命周期、§19.48 App默认Host来源、§19.49 Client只读目录/UI清理已接；§19.50固定目标准备、§19.51可选发送编号、§19.52拥有型换代来源、§19.53发布接缝及§19.54 Router内部可选装配已验证，双配置5651通过；默认App未启用新装配。§19.55请求纯codec及无资源单槽准入5999通过，§19.56内部拥有型执行器6065通过，§19.57显式本地整帧成功及帧代际关联6103通过，§19.58请求代际首发结算6154通过，§19.59成功回执纯合同与显式占槽6692通过，§19.60显式会话写者6729通过，§19.61请求reader/单handler停止监督6780通过，§19.62真实自动协调6812通过，§19.63 Client真实sender与唯一reader严格回执关联6847通过；先有真实sender而非fake Arm才可建立pending资格。§19.64内部真实视频/JPEG/STA Image.Source逻辑提交屏障6869通过，已不再只元数据；LatestDisplayChange本身仍是回执元数据，LastPresented非物理scanout。§19.65内部CreateMultiDisplay多屏GDI采集6922通过，53新增与旧Transport测试54字节同步例外详后；默认App仍未启，不将内部自动化当双机验收。下一片接App→Capture/Sessions internal friend及目标准备、Host每会话writer/prepare、Client attach前enable及三参Presenter、窗口请求/忙状态/解绑；须传递真实认证session身份，禁共享lastSessionId或Guid假身份。目录每连接单发，冻结settings。当前无需人工前置，M6后续实机验收保留。不重验M4/M5、不启动M7、不改网/防护/权限、不覆盖旧ZIP。
> **启动补记（2026-09-24）：修复版ZIP及说明已交付；本机系统应用控制拦截经用户明确确认后处理，指定安装目录的验收器窗口已启动，并由独立进程查询确认非零窗口句柄和响应正常。** 本次没有改产品代码或网络；仅证明启动成功，不代表完整GUI/两机验收通过，系统防护处理不属于产品功能。
> §18.9/§18.10 保留阶段 4 与评审等待历史，不再是当前停点。
>
> 上轮（**M4 · 阶段 3「服务端认证状态机」**）**动了代码**：
> 新建 7 个产品文件（`ConnectionSecurityContext` / `ControlAuthContext` /
> `ControlAuthSession` / `ControlPreAuthHandoff` / `FailedAuthLimiter` /
> `LocalApprovalGate` / `SessionRegistry`）+ 认证测试 `ControlAuthSessionTests.cs`
> （25 用例）+ 6 个修改文件（衔接层改写与指纹冻结）；提交 `760e950`
> （14 files，+2780/−89）；全量 **902 PASS / 0 FAIL**（Transport 460→485，+25）。
> 变异验证 ×4（M4 证明纵深防御「全拆才红」形态）；
> 合同级决定已立 **ADR-041**。逐条见 §18.8。
>
> 上轮（2026-09-22）：M4 阶段 2「认证消息帧（JSON 严格解析）」（ADR-040）见 §18.7；
> 阶段 1「transcript + HMAC proof 纯函数核心」+ 同日重定位 Security → Transport
> （ADR-039）见 §18.6；更早：阶段 0「衔接盘点与定案」（ADR-037/038 + ADR-027 落地）
> 见 §18.5，第二轮外部评审回收见 §18.4。

---

## 1. 当前状态

- **当前里程碑：M5按2026-10-12用户稳定反馈收口；M6进行中。** 最新完成片§19.73 自动画质遥测接缝与决策核心（ADR117），代码4fd4099已普通push并核remote一致，9files/+450/-1（1新增生产+3生产改+3新增Acceptance测试类）；新增14项，initial/两次恢复定向各14PASS，五正式变异controller-single-sample-switch / controller-bad-becomes-upgrade / controller-good-becomes-downgrade / controller-zero-fps-counts / controller-keeps-unknown-state分别2 / 2 / 2 / 1 / 1个原Assert红并精确恢复。final-v1于12:15:43–12:19:42 +08:00四Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各7024 PASS / 0 FAIL / 0 SKIP（Integration3/Core197/Sessions300/Security66/Capture296/Protocol303/Acceptance604/Transport5255）；独立audit-v1原进程 / 外层 / 主线程exit0，449源 / 179观察原件 / 40TRX / 28118唯一execution，旧7010完整身份 +14 / -0。接缝只暴露已发布的active代际（ActiveProducer/HasReplacementInFlight，换代空窗为null，不冒充候选）与本次实际选中DisplayInfo；决策核心按会话累积RTT与实际帧率，连续3个同向样本才在low-latency/balanced/high-quality间升降一档，无样本、未启动帧率、未知/手工档位一律不改档。本片不接周期换代、不改运行中管线，画质调控尚未在产品会话生效。同日前一片§19.72 真实Control ping/pong与按会话RTT 9fb249f/7010：Host复用目录/回执唯一写者发ping（写槽忙则跳过、不排队），Client唯一monitor读ping并由无队列pong单槽回echoedSeq，Host按回显序号结算并按真实sessionId登记/注销遥测；产品Host经WithControlPing显式启用（2s探测 / 5s清理），旧调用方不多发帧。M6任务清单余auto quality周期换代接线（消费RTT与实际帧率、按预设名创建候选并走ReplaceableVideoFrameSource换代）。final-v1四Rebuild零警告错误、六dotnet及worker/launcher完整wait0，Debug/Release各6984 PASS / 0 FAIL / 0 SKIP（Core197/Security66/Protocol303/Integration3/Transport5229/Acceptance590/Capture296/Sessions300）；独立audit-v1原20792 / 外层 / 主线程exit0。预设名与档位一一对应且严格解析（未知名拒绝而非静默回落），档位数值以VideoQualitySettings三个静态预设为唯一事实来源；本片不改Core、不改Clamp语义、不改协议、不改App/Host默认行为。前一片§19.69实际帧率指标dcd5eea/6965同日完成。M6任务清单余ping RTT与auto quality简单控制器。final-v1四Rebuild零警告错误、六dotnet及worker/launcher完整wait0，Debug/Release各6965 PASS / 0 FAIL / 0 SKIP（Core197/Security66/Protocol303/Integration3/Transport5229/Acceptance571/Capture296/Sessions300）；独立audit-v1原46908 / 外层 / 主线程exit0。实际帧率以Stopwatch测量，未在FramePipeline构造或Start新增TimeProvider调用（既有7项时钟失败测试断言TimestampCalls==0且须保留注入时钟失败的原异常）；EncodedFrames用精确断言，ActualFps只写弱断言（0帧为0、有帧后>0、读取不清空历史）。本片只暴露指标，未接auto quality控制器，不等于目标帧率、显示帧率或网络发送帧率。前两片遗留已回填：§19.66产品侧切屏接线6940、§19.67每会话画质底座6951、§19.68每会话画质接线6961。新增53（45合成native+8真实offscreen），三轮定向各231PASS；四正式mutation target-dc/post-snapshot-final/pre-stability/pixel-budget分别3/21/1/1原Assert红恢复，原post-snapshot因CS8602 warning gate未测试，不算kill。final-v2四Rebuild零警告错误、六dotnet及worker/launcher完整wait0，Debug/Release各6922PASS/0FAIL/0SKIP；独立audit-v2原4552/外层/主线程exit0。final-v1 Debug6922绿、Release旧Transport测试line277 Requests为空；仅补await f.Dispatched.Task.WaitAsync(Guard)这54字节，原断言与身份保留、Transport生产不改，Capture生产和测试冻结，唯一逆变换核固定旧hash。audit-v1跨项目Gdi substring误选Acceptance用例的失败保留，v2按原argv project/storage边界筛选。内部多屏能力已写成，默认primary API与App原路径保持；真实offscreen非真实副屏/混合DPI/热插拔或双机验收，§19.64显示屏障仍非物理scanout。
- **下一步（Next）：在§19.65已验证的internal CreateMultiDisplay与§19.64显示提交屏障上接App产品路径，不是重写GDI或仅加按钮。** 为App接入Capture/Sessions internal friend与固定目标准备工厂；Host按每个真实认证会话装配DisplayRequestPrepare/UseSessionWriter，不得用共享lastSessionId或临时Guid冒充认证身份，若现有接口缺session须沿内部接缝传递。Client在认证最终交付后、attach/monitor前EnableDisplaySwitching，使用三参RemoteVideoPresenter；窗口补真实RequestDisplayAsync、busy状态及停止/请求解绑。冻结settings，不加配置热写；目录仍每连接单发，不动态刷新。Client必须由真实RequestDisplayAsync建立pending，禁止fake Arm或仅Feed回执冒充请求资格。内部现已让session/请求epoch/FrameId贯穿有界队列、真实JPEG与Dispatcher，回执/视频先后均重评单最新候选，Image.Source前同父gate复检；回执F仅下界，LastPresented仅逻辑提交历史，非物理scanout。默认App仍未启，不新增reader、不重连单次Video租约，不将内部测试当双机验收。§19.62已由internal DisplayRequestPrepare自动组装每会话协调器与真实tracked Router，无source请求fail-close；默认App未设置prepare，尚待上述产品接线。 §19.61显式handler非null时已读取request_display并监督原handler任务；只在派发检查点核Ready/父资格，具体业务仍需handler走原admission/Replace。handler忙时第二完整帧fail-close，不是排队或可恢复拒绝。 §19.60已经把可选认证后目录与回执接到同一真实流写者；UseSessionWriter init默认为false且保留原两参构造，默认App不启用。目录成功Ready/实际Catalog仍为历史入口，调用方须用实际目录seq建准入；序号只覆盖post-auth目录/回执，不声称包含握手。默认handler=null的原holding仍把额外数据视越界；显式handler路径已接reader但不是自动产品切屏。提交先赢后Stop仍可物理迟到，外层先close/cancel再完整join，source/视频sender不反等回执。旧两个执行器模式保持原行为；Binding/FirstSend非未来授权或资源join。 §19.56内部执行器已完成prepare委托/迟到候选归属/父gate最终Replace/完整join，但尚未接Sessions固定目标工厂或live；外层仍须先独立停止source等依赖再join执行器，父关闭须显式RequestStop。接受请求、IsCurrent快照和Replace完成均非发送/显示成功证明，不提前发布display_changed。不新增读者，不重连单次Video租约。Router内部可选装配已完成：ACK完整退出/初装成功/sender原worker登记后发布，退出先摘槽并独立通知close/cancel/source Dispose，完整join。source早期终态/唯一权威错误槽已接，不静默改变旧Router或App默认路径。 internal拥有型换代来源、固定目标准备及可选编号不等于live切屏；不能重连已消费Video租约，也不能把编码首帧当实际发送边界。Client只读目录不等于视频选屏状态；元数据seq/timestamp不是拓扑世代或授权。当前没有双机人工前置阻塞，但后续M6实机验收保留。 App默认Host已通过WithDisplayCatalogSource装配委托内GDI枚举，未配置的其他context仍默认null；目录Abort/Host stop/holding结束与Router关闭监督不变，保持success完整flush→Register→秘密清理→原holding→目录发送、唯一读/逆向额外数据拒绝、先通知close及撤销父资格，再完整join并在Dispose后取VideoCompletion。Client monitored/public attach已由原唯一读者串行消费host_status；Control-only Client不自行新增monitor，public CreateWithVideo六参数合同不扩张。随后推进切屏和每会话画质。已完成链路固定为TLS/pinning→Hello2→Challenge2→Host Offer→Client Accept→clientProof V2→真实审批→serverProof V2→新版父资格；禁止V2失败fallback V1。完整LegacyV1仅是未认证声明，不能覆盖device/实际TLS pin/proof/密码/审批错误；EOF/timeout不确诊旧端。Offer/Accept消耗原绝对预算，agreement本身不是授权。VideoHello/ACK protocol1、VIDEO-V1、83字节transcript、第二TLS/LRVF不变。不重做M4/M5人工项，不启动M7，不改网/防护/权限、不执行旧Undo、不覆盖旧ZIP。
- 已完成：M0 → M1 → M1.1 → M1.2 → M1.3 → M2 → M2.1 → **M3 → M3.1 → M4 → M5（用户确认）**。M6进行中，不能将纯核冻结称M6整体完成。
- 版本：`0.1.0-m2`（本轮**未**推进版本号）
- **Last code commit：`4fd40993bc798c4a9e1be8ea4130ed187295d50c`**（2026-10-28 自动画质遥测接缝与决策核心，9files/+450/-1：1新增生产+3生产改（含2个csproj友元/引用）+3新增Acceptance测试类）。已普通push并核remote一致，详§19.73。
- **Working tree at validation（最新）：2026-10-28 自动画质遥测接缝与决策核心最终树。** 新增23项，initial/restored-v1/restored-v2三轮定向各23 PASS / 0 FAIL / 0 SKIP；五正式变异各1个原Assert红并精确恢复。final-v1于10:21:34–10:26:23 +08:00四Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各7007 PASS / 0 FAIL / 0 SKIP（Core197/Security66/Protocol303/Integration3/Transport5252/Acceptance590/Capture296/Sessions300）。summary 10496字节 / SHA `99c6e0c4ba33588fc4190542927efde383a0157761e5a2372ea037e4eccdc746`。audit-v1于10:26:38–10:26:43 +08:00原审计进程52524 / 外层 / 主线程exit0；report.json 1034539字节 / SHA `f0b228485e7567b7c7c036a0722ff886697c7d0c8e146a5c0cd5d4ed783c5a44`；441源 / 180观察原件 / 40TRX / 28056唯一execution，旧6984完整四字段身份 +23 / -0，前片三份成品（画质预设ZIP/TXT/SHA）摘要保持。本片未修改任何旧测试，无同步修正桥。RTT由注入时钟确定测量（ManualDeadlineClock推进），不是真实网络往返，也不等于双机/真人/实屏验收。
- **Working tree at validation（命名画质预设历史）：2026-10-23 命名画质预设最终树。** 新增19项，initial/restored-v1/restored-v2三轮定向各19 PASS / 0 FAIL / 0 SKIP；五正式变异分别1 / 7 / 1 / 3 / 1个原Assert红并精确恢复。final-v1于17:04:38–17:08:53 +08:00四Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6984 PASS / 0 FAIL / 0 SKIP（Core197/Security66/Protocol303/Integration3/Transport5229/Acceptance590/Capture296/Sessions300）。summary 10643字节 / SHA `7daa100b2e6bbc0293d1115bb8fa8c61b8e5182fda94797ad212fd0819d48a59`。audit-v1于17:09:18–17:09:23 +08:00原审计进程20792 / 外层 / 主线程exit0；report.json 1025903字节 / SHA `ffc87cd91faf3211fa2821bbe9fcdfc79df62960765cd1293f8573ee7abf2497`；436源 / 180观察原件 / 40TRX / 27968唯一execution，旧6965完整四字段身份 +19 / -0，前片三份成品（实际帧率指标ZIP/TXT/SHA）摘要保持。本片未修改任何旧测试，无同步修正桥。首轮final-v1曾出现既有`HostDisplayRequestReaderTests.StopBeforeOriginalContinuationExitNotifiesButNeverDispatches`在整仓并行负载下的抖动失败（§19.66已记录过同一条）：单独复跑该用例2项全绿、本片未触及Transport任何代码，该缺陷轮连同收据一并删除后整体重跑final-v1，此事实在此显式记录，不隐藏。预设解析与按会话设档均在本机受控测试中验证，不是界面预设选择、双机/真人/实屏验收。
- **Working tree at validation（实际帧率指标历史）：2026-10-23 实际帧率指标最终树。** 新增4项，initial/restored-v1/restored-v2三轮定向各4 PASS / 0 FAIL / 0 SKIP；五正式变异各1个原Assert红并精确恢复。final-v1于13:59:07–14:03:30 +08:00四Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6965 PASS / 0 FAIL / 0 SKIP（Core197/Security66/Protocol303/Integration3/Transport5229/Acceptance571/Capture296/Sessions300）。summary 10538字节 / SHA `e0387f13e8debf531111dd8d6ebddb86ecf50a0b2dd72dc4438685ae86e6b985`。audit-v1于14:03:55–14:04:00 +08:00原审计进程46908 / 外层 / 主线程exit0；report.json 994301字节 / SHA `ecd067530ce4d7a04415e169e022ae5b0c431d3e78ce02608d5fdb5b2152cc65`；434源 / 180观察原件 / 40TRX / 27869唯一execution，旧6961完整四字段身份 +4 / -0，前片三份成品（画质接线ZIP/TXT/SHA）摘要保持。本片未修改任何旧测试，无同步修正桥。两处脚本缺陷轮已显式记录：首轮counter变异用注释替换触发CS0649告警门禁（非测试红，不算kill）已改为赋零写法；xunit v3的Assert.True(cond,message)只上报自定义消息，故原断言改由「消息含Assert.或堆栈落在本片新增测试类」双重认证，两轮缺陷产物删除后完整重跑。实际帧率是本机受控夹具下的编码计数与Stopwatch比值，不是双机/真人/实屏/物理多屏验收。
- **Working tree at validation（内部多屏GDI采集历史）：2026-10-21 内部多屏GDI采集最终树。** 新53（45合成native+8真实offscreen），三轮定向各231PASS；target-dc/post-snapshot-final/pre-stability/pixel-budget四正式变异分别3/21/1/1原Assert红恢复。原post-snapshot的CS8602 warning gate未运行测试，不算kill。final-v2于11:22:49–11:26:54+08:00四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6922PASS/0FAIL/0SKIP（Capture296/Transport5229/Acceptance543，其它原同）。summary10518字节/SHA `fd1d4036b66f7963cb61c6c880ab8003f4c5681120d543dd05edb3f7742b8b71`。audit-v2于11:30:13–11:30:17+08:00原4552/外层/主线程exit0，report1177362字节/SHA `805e61ef3de63184677a5862d2300825e62cc28368dcbed7d10b7a5624dd06d6`；428源/214原件/55TRX/42151execution，旧6869完整身份+53/-0。audit-v1跨项目Gdi substring误计Acceptance用例为Capture定向，失败保留；v2按原argv project/storage边界正确筛选。final-v1 Debug6922绿、Release旧HostDisplayRequestReaderTests.IdleReadAheadHasNoTimerAndStopJoinsItsOriginalRead在line277因Requests为空失败；独立handler与heldEntered无happens-before，仅增加await f.Dispatched.Task.WaitAsync(Guard)这54字节，原断言保留、Transport生产未改，以固定hash唯一逆变换认证，Capture生产和测试真正冻结。内部多屏已实现，默认App仍旧，offscreen不是物理副屏/混合DPI/热插拔或双机验收；详§19.65。
- **Working tree at validation（显示提交屏障历史）：2026-10-20 内部显示提交屏障最终树。** initial-v4/restored-v2定向各35PASS，新增22全部Acceptance，原13默认呈现保持；四最终mutation frame-floor/pending-gate/receipt-size/monotonic-id分别1/1/1/2原Assert红恢复，首轮同mutation仅history。final-v2于19:20:23–19:24:28+08:00四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6869PASS/0FAIL/0SKIP（Acceptance543、Transport5229，其它项目不变）。summary10453字节/SHA `aed523d3963a82a861514d7af2420398ffcfd9f080c830e16df12e071859a41b`。独立audit于19:24:59–19:25:05+08:00原60052/外层/主线程exit0，report394170字节/SHA `1c5ca69d747b29969c285797a0e116613ef7f52911d475066d84ffdbe63c7993`；426源/285观察原件/61TRX/41361execution，旧6847完整四字段身份+22/-0，前片三成品保持。v1新4warning、v2错误直接await断言watchdog、final-v1 Debug通过/Release1FAIL均保留；只改新fixture为FlushPublished TCS<Task>后完整重跑，生产未改。内部真实视频/JPEG/STA Image.Source已接，不再仅元数据；LastPresented非物理scanout，默认App未启，非双机验收。详§19.64。
- **Working tree at validation（Client请求/回执历史）：2026-10-20 Client真实请求与回执关联最终树。** 最终v3/恢复v1定向各208/208、新增35；16:54:54–16:59:02+08:00四Rebuild零warning/error，六dotnet及worker/launcher完整wait0。Debug/Release各6847PASS/0FAIL/0SKIP（Transport5229，其余项目数不变）；summary10471字节/SHA `6e274bff21fd02ce8c05c234bfe8240bfb9a6385c334b23e98fbe8bbb0555450`。四变异request-sequence/write-budget/reply-correlation/operation-error分别1/1/3/1原Assert红恢复；custom Assert.False消息不含Assert导致driver assert中断，独立audit凭固定源1016/栈/消息认证真实断言，未改测试、未伪timeout。audit-v1因合法旧格式outer receipt缺log字段KeyError失败保留；v2仍逐行强核日志等于launcher，log字段有则核SHA。17:00:49–17:00:53+08:00 audit-v2原31532/外层/主线程exit0，报告341948字节/SHA `1b016873c74014e2dbb13deafb7b600883b0a9c3200ea2de2b6344a7c51fcf64`；423source/175observed/39TRX/27948execution，旧6812完整四字段身份+35/-0，前片3deliverable保持。仍无视频/UI屏障，详§19.63。
- **Working tree at validation（自动协调历史）：2026-10-20自动协调树，`outputs/m6-request-coordinator-20261020/switch-executor-coordinator-final-v1/summary.json` passed/exit0。** 15:21:46–15:25:48+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0；双配置各6812PASS/0FAIL/0SKIP，Transport5194。summary10579字节/SHA `179e72a308ee3e13876518f6c19bcf11ebf8bd2ed5e2a691ce349fe3c3b46114`。15:28:52–15:28:56独立审计v2原进程44180/外层/主线程exit0，421源/176原件/39TRX/27762execution、旧6780身份+32/-0；前片3成品保持，定向191/191、四变异1/2/1/1红恢复。详§19.62。
- **Working tree at validation（reader历史）：2026-10-20Host请求读取最终树，`outputs/m6-request-reader-20261020/switch-executor-reader-final-v2/summary.json` passed/exit0。** 11:12:43–11:16:54+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0、主线程回收；双配置各6780PASS/0FAIL/0SKIP（Transport5162），summary10474字节/SHA `103b3f7198f41be947472c9b4ed2afe3b5da3e76b7a176305935cc81417f898f`。11:17:49–11:17:56独立原进程57500/外层/主线程exit0，旧6729身份+51/-0，418源/312原件/58TRX/34950execution；前片四交付保持。最终定向196/196、四变异各1原断言红恢复；历史fail/timeout未当kill。详§19.61。
- **Working tree at validation（写者历史）：2026-10-16Control唯一写者最终树，`outputs/m6-control-writer-20261016/switch-executor-writer-final-v2/summary.json` passed/exit0。** 17:18:11–17:22:21+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，主线程完整回收；16TRX双配置各6729PASS/0FAIL/0SKIP（Transport5111）。summary10467字节/SHA `4bb1dbc86423859a36d0841c38504dccd9e90e6b69e2941072dfa0d1d4de251c`；17:24:25–17:24:36独立审计原进程24200/外层/主线程exit0，415源/295原件、56TRX/34213execution，旧6692完整身份+37/-0、18旧ZIP保持。最终定向157/157、六变异1/1/2/1/1/1红恢复，初版警告门禁与旧反射3失败及首组六变异全部作为历史保留核字节逆桥，非最终证据。受控认证同流实际目录/回执，不是默认App/Host请求reader或双机，详§19.60。
- **Working tree at validation（回执历史）：2026-10-16Control回执占槽树，`outputs/m6-display-reply-20261016/switch-executor-reply-final-v1/summary.json` passed/exit0。** 14:59:00–15:03:14+08:00四Rebuild零警告错误，六dotnet/worker/launcher完整wait0，主线程任务完整回收；16TRX双配置各6692PASS/0FAIL/0SKIP（Transport5074），summary10448字节/SHA `3e157379313a83e7c76a2e530c6a1df2af9adce5a4332e4f2ddad1bc95e9addd`。15:04:37–15:04:46独立审计原进程11180/外层/主线程exit0，412源/171原件/40TRX/27291execution，旧6154完整身份+538/-0；17旧ZIP保持。正式定向722/722、六变异44/2/1/7/1/1原断言红恢复，前两包装器自定义断言误分类已精确核实。未接真实Control writer/live，详§19.59。
- **Working tree at validation（结算历史）：2026-10-16请求代际结算树，`outputs/m6-send-settlement-20261016/switch-executor-settlement-final-v1/summary.json` passed/exit0。** 11:36:16–11:40:26+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，16TRX双配置各6154PASS/0FAIL/0SKIP（Transport4536）。summary10551字节/SHA `3f589fef9d2d9e92aea630c5ddfafadcbb34c1ebe9de7183666fab6c9fa5865c`；主shell任务句柄本次无法查询，原stdout及launcher完整wait0实读保留，不捏造外层回收。11:50:42–11:50:49独立审计原进程35628退出0、外层与主线程wait0；409源/159原件/39TRX/25385execution，旧6103身份+51/-0，原件退出后保持，16旧ZIP摘要保持。定向308/308，五变异21/18/7/15/3原断言红恢复；非Control回执/默认live或双机，详§19.58。
- **Working tree at validation（发送边界历史）：2026-10-16整帧发送边界树，`outputs/m6-send-boundary-20261016/switch-executor-send-boundary-final-v1/summary.json` passed/exit0。** 10:33:19–10:37:21+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，主线程后台回收exit0；16TRX双配置各6103PASS/0FAIL/0SKIP（Transport4485）。summary10602字节/SHA `03a7ca9cb227b337165b98bcb59974abd7744ea6b73a6e04d9fa3ebf33753764`。10:37:47–10:37:54独立审计原进程35812退出0、主线程wait0；408源、157原件退出后保持、39TRX/24992唯一execution、旧6065完整身份+38/-0。正式定向233/233，五变异24/2/3/4/15原断言红恢复。默认产品未启用，受控流非TLS/双机/GDI/显示验收，详§19.57。
- **Working tree at validation（执行器历史）：2026-10-15 切屏请求执行器树，`outputs/m6-switch-executor-20261015/switch-executor-final-v1/summary.json` passed/exit0。** 19:39:56–19:44:06+08:00，四Rebuild零警告错误、六dotnet/worker原launcher收据完整wait0；16TRX双配置各6065PASS/0FAIL/0SKIP（Transport4447），summary10342字节/SHA `71c1b78a2a7aa2cd34de5a274959b76230d84bd8a762e1f8afecd17696321c43`。主shell后台句柄丢失，未捏造该句柄成功回收；保留原launcher真实wait链。19:52:59–19:53:02正式独立审计原进程exit0且主线程wait0，旧5999完整身份+66/-0，407源/40TRX/24680execution/195观察原件保持。正式初始/恢复各181、五变异各1红并恢复；历史编译失败/v2绿经字节桥核实。内部受控Task非live/实屏/双机，详§19.56。
- **Working tree at validation（请求准入历史）：2026-10-15 切屏请求准入树，`outputs/m6-switch-contract-20261015/switch-contract-final-v1/summary.json` passed/exit0。** 17:43:32–17:47:48+08:00，四Rebuild零警告错误、六dotnet及launcher/worker完整wait0，16TRX双配置各5999PASS/0FAIL/0SKIP（Transport4381）。summary10335字节/SHA `2e52eec1f6f903d09aa03f24dfafcd1948d7296dd17531396c5769fed18fa8c5`。独立审计原进程exit0、主线程wait0：旧5651完整身份+348/-0，405源、40TRX/25267 execution、183观察原件退出后保持。定向491/491、四变异1/2/1/1原断言红并恢复；v1/v2历史测试字节逆桥核对。无网络消息接线/实际切屏，受控producer/Task非双机/GDI/实屏，详§19.55。
- **Working tree at validation（Router换代装配历史）：2026-10-15 Router换代装配树，`outputs/m6-router-generation-20261015/router-generation-final-v1/summary.json` passed/exit0。** 15:38:21–15:42:38+08:00，四Rebuild零警告错误、六dotnet及launcher/worker完整wait0，16TRX双配置各5651PASS/0FAIL/0SKIP（Transport4033）。summary10378字节/SHA `be6e72a44781ee3933c69bb8eac375ed281d9af1908ee3cd7a6034441671bdb4`。独立核验exit0：旧5615完整身份+36/-0，402源、38TRX/23553 execution、正式v2核验169观察原件退出后保持；初始/恢复339、三变异各1红并精确恢复。首轮编译失败/第二轮历史绿保留；受控Task/Stream非双机/真人/GDI/TLS/实屏，详§19.54。
- **Working tree at validation（发布接缝历史）：2026-10-15发布接缝树，`outputs/m6-video-publication-20261014/publication-final-v1/summary.json` passed/exit0。** 13:53:29–13:57:49+08:00，launcher→worker→六dotnet均完整wait0，四Rebuild零警告错误、16TRX双配置各5615PASS/0FAIL/0SKIP（Transport3997）。summary10264字节/SHA `38f6c5e02bc9328454d76d25c8ba12cf8fc62c6eb3706c4a27834a8f7a37b123`。独立核验exit0：旧5584完整四字段身份保留、+31/-0，400源，39TRX/22707唯一execution。初始/恢复定向150，五变异2/1/未击杀/1/1且完整wait后恢复。受控producer/Task，不是Router装配、GDI/TLS/双机/物理多屏验收；详§19.53。接手阶段未重新运行构建/测试，本条仅归位既有历史收据。
- **Working tree at validation（换代来源历史）：2026-10-14换代来源树，`outputs/m6-video-generation-20261014/generation-final-v1/summary.json` passed/exit0。** 四Rebuild零警告错误、六dotnet原命令及launcher/worker完整wait0；19:28:34–19:32:43+08:00，16TRX双配置各5584PASS/0FAIL/0SKIP（Transport3966，其余保持），summary10248字节/SHA `c378ce601e8690f7f799859ba77f04e630ecdba402a71a99ae28df76c660be7b`。独立核验exit0：旧5536完整四字段Counter+48/-0，399源175观察原件保持，39原TRX/22638唯一execution；初始/恢复定向195，五变异1/2/1/1/2原断言红且完整wait后精确恢复。受控producer/Stream，不是GDI/TLS/物理多屏切换；不把换代完成当发送/显示屏障，详§19.52。
- **Working tree at validation（可选发送编号历史）：2026-10-14可选视频发送编号树，`outputs/m6-video-sequence-20261014/sequence-final-v1/summary.json` passed/exit0。** 四Rebuild零警告错误，六dotnet原命令及launcher/worker完整wait0；16TRX双配置各5536PASS/0FAIL/0SKIP（Transport3918，其余保持），summary10208字节/SHA `0178e850d9b3ab482d9481ee78d7cb3bfffb0f46c07a43764e0cd7b9e1955cc2`。独立核验exit0，旧5475完整四字段Counter+61/-0、39TRX/22334唯一execution、397源174观察原件保持；初始/恢复定向147，五变异3/4/1/6/2原断言红且完整wait后精确恢复。受控Stream/来源非真实TLS/实屏切换，详§19.51。
- **Working tree at validation（固定目标准备历史）：2026-10-14固定目标准备树，`outputs/m6-display-target-20261014/target-final-v1/summary.json` passed/exit0。** 四Rebuild零警告错误，六dotnet命令及外层完整wait0；16TRX双配置各5475PASS/0FAIL/0SKIP（Sessions285，其余保持）。397源前后相同，清单65715字节/SHA `1dae7a80059e8d112913743875ab5e42f63591b94fd720def855e91451b30460`；summary10167字节/SHA `e2883ca652d85bc68d1fce133782b9ca92d75b2e195108ecec1593dbcc81fc02`。独立核验实际exit0，5434完整Counter+41/-0、39原TRX/22163唯一execution、173观察原件保持；初始/恢复工厂各157及五变异原断言/字节/退出/恢复核实。受控capture/encoder配真实pipeline，不是实际GDI/物理多屏/双机/TLS联合切屏；大尺寸仅prepare/Stop，详§19.50。
- **Working tree at validation（Client公开目录历史）：2026-10-14 Client只读目录显示树，`client-projection-final-v2-20261014/summary.json` passed/exit0。** 四Rebuild零警告错误、六命令及外层实际wait0，16TRX双配置各5434PASS/0FAIL/0SKIP（Transport3857/Acceptance521）。397源清单65715字节/SHA `3074e9a5a6718913d592ae32d9e3b6bc04ed9ca0722b14d1d9721f56f9fdee2e`；summary10533字节/SHA `a8a1b49d820ba59e51d7e358e85e687841757081846a4a5efdd9ecd8040c6736`。独立核验exit0，旧5410完整Counter+24/-0，55严格解析TRX/28711唯一execution，357观察原件及两次397源枚举保持。另六历史失败阶段仅核原件/wait保持，不计正式kill。首轮full Debug5432PASS/2FAIL、Release未执行原样保留；正式变异v4到最终仅两处getter白名单增加，独立逆替换SHA衔接。产品用例为真实同机双TLS、显式测试审批gate、合成两屏与2x2 JPEG，不等于真人/双机/物理多屏，详§19.49。
- **Working tree at validation（Host公开来源历史）：2026-10-14 Host公开来源与产品装配树，`host-send-public-final-20261014/summary.json` passed/exit0。** 四Rebuild零警告错误、六命令及外层实际wait0；16TRX双配置各5410PASS/0FAIL/0SKIP（Transport3846/Acceptance508）。393源清单64996字节/SHA `ae8461489250ce2d165de54dfe8ebb071b3b9f5aef86c01b5423f87e58c195df`；summary10457字节/SHA `1234f4a187aef9b34e199686a0ee1229930277e489b05bc6dcff68480f3d33b1`。独立核验exit0：旧5383完整Counter+27/-0，39原TRX/21738唯一execution，168证据及两次393源枚举保持。产品测试为真实单机Control TLS、WPF控件自动审批和只读GDI，不是真人/双机/视频TLS联合验收；快照非连续锁、PID/wait非OS签名，详§19.48。
- **Working tree at validation（认证后目录生命周期历史）：2026-10-13认证后目录生命周期树，`host-send-catalog-lifetime-final-20261013/summary.json` passed/exit0。** 四Rebuild零警告错误、六命令及外层wait0，双配置各5383PASS/0FAIL/0SKIP（Transport3822）；391源清单64638字节/SHA `735f76df3cca598e8175549aa7150659a5758b2b27d53491f2f5612ffbc49635`；summary10638字节/SHA `62a715e5ddb51940e3f58d97502d19a1fcd9d88117ee9ab3d8950d997f515233`。独立核验exit0：5361完整Counter+22/-0，37原TRX/22012execution、146证据与两次当前源枚举保持。快照非连续锁、PID/wait非OS签名，不是默认产品发送或双机验收，详§19.47。
- **Working tree at validation（Router关闭监督历史）：2026-10-13 Router关闭监督树，`host-send-router-close-final-20261013/summary.json` passed/exit0。** 四Rebuild零警告错误、六dotnet子命令及外层实际wait0；16TRX双配置各5361PASS/0FAIL/0SKIP，Transport3800。388源清单64095字节/SHA `611a110eee11551dd1b17aff1a9640adb4c71d49d290aec8600934e3b47045f7`；summary10568字节/SHA `6054d25a590cd39830e5c2781aac889a03cb52f1632d10d796a688d343431297`。正式独立核验exit0，旧5344完整身份全部保留、新增17/删除0；36原TRX/21895唯一execution及126原件保持。快照非连续锁、wait/PID非OS签名，本片不是目录自动发送或双机人工，详§19.46。
- **Working tree at validation（Host发送底座历史）：2026-10-13 Host单次目录发送底座树，`host-send-final-20261013/summary.json` passed/exit0。** 四Rebuild零警告错误、六dotnet子命令及外层实际wait0；16TRX双配置各5344PASS/0FAIL/0SKIP，Transport3783，其余项目保持。387源码前后相等，清单SHA `3cc23024707ab777dbaa7e790bf2c8a58765b9c21b3a824340a60634dd8566c7`，summary SHA `e3643311c1c3b58b882b68db38b2a3bee3df5dd1538483fa685846491fb24901`。独立核验/提交/交付收据见§19.45；快照非连续锁，范围含tools不含docs/memory/HANDOFF；不是产品Host live或双机人工。
- **Working tree at validation（Client接收历史）：2026-10-13 Client目录接收树，`outputs/m6-live-v2-20261012/catalog-final-v1-20261013/summary.json` passed/exit0。** 四Rebuild零警告错误、六dotnet子命令及外层任务实际wait0；16TRX双配置各5279PASS/0FAIL/0SKIP，Transport3718，其余项目与5246基线相同。385源码项前后及独立当前枚举相同，清单SHA `3e33ade0e585809ced12928d7a2fb32deb945f9751119ef13e2ff4b788c99036`；summary SHA `250f205c863e14481d175a83fb554bdaff40616378d450c6ac377724bb394d4d`。完整日志/PID/时窗核验、5246补充身份差集/旧包绑定、四变异核验均exit0；35新增/2消失身份实例，均count1，详§19.44。快照非持续锁，不含docs/memory/HANDOFF，不冒称全历史审计或双机人工。
- **Working tree at validation（V2-only接线历史）：2026-10-13未提交V2-only完整认证接线及补测树，`outputs/m6-live-v2-20261012/final-v1-20261013/summary.json` passed/exit0。** 四Rebuild零警告错误、六dotnet子命令及外层任务实际wait0；16TRX双配置各5246PASS/0FAIL/0SKIP（Core197/Security66/Protocol303/Integration3/Transport3685/Acceptance505/Capture243/Sessions244）。384项源码前后一致，范围src/tests/tools/scripts及除HANDOFF外根文件，排除bin/obj/__pycache__，不含docs/memory/HANDOFF；清单SHA `72d19c6112c64f1b3676f88f808c23be80e89167f4cebc2a04e529502ce330cf`。正式独立核验实际exit0，报告SHA `f29e54201a3415fa0c5cf4945e17c794d6bec6476515e1354875344d89073b8f`；旧5143→新5246为新增115/消失12实例，新增107唯一键，完整Counter差集见报告。恢复定向1055/134、八变异结果和两次失败保留；文档编辑不在源码快照范围，不称连续锁/全历史审计/双机人工验收，详§19.43。
- **Working tree at validation（Challenge2身份历史）：2026-10-12未提交Challenge2身份消费子片，`outputs/m6-challenge-admission-20261012/final/summary.json` passed/exit0。** 四Rebuild零警告错误，六dotnet子命令实际wait0，16TRX Debug/Release各5143PASS/0FAIL/0SKIP（Transport3584）；原5112+新31身份精确相等。355项枚举源码前后保持，清单SHA `820dd7f54cb1edc27cdba5b16a246c07b9d741a7e2a7efbe26b3681c051ec7b8`；范围为src/tests/scripts及除HANDOFF外根文件，排除bin/obj/__pycache__，不含tools/docs/memory。独立核验报告12469字节、SHA `9a04b94673bab229b6e6e309a860c9b3a666bd883c43dae124804e43f4c18ab2`，实际exit0。31旧实现红灯均为入口缺失断言，行为敏感性另有四变异4/4/4/3失败并精确恢复。本片未做上一片100/7护栏、全历史证据边界或post-handoff before/after；不冒称同强度正式审计。详§19.42。
- **Working tree at validation（Hello2/Challenge2历史）：2026-10-12未提交Hello2/Challenge2独立合同，`outputs/m6-wire-v2-20261012/final-v1/summary.json` passed/exit0。** 四Rebuild零警告错误，16TRX双配置各5112PASS/0FAIL/0SKIP（Transport3553，原5019+新93精确身份）。376源码冻结SHA `bfd00f3a1a4965f9ae2ddbaa785f363fa090a37430c38b3d7bf972391ed14dd5`，final摘要SHA `954d0454f82a4519a81383a8e27af79ce43626bb8f29d35eae506348d6a780fd`；外层launcher27484→runner8928真实wait0已回收。100项审计自测、7项交接护栏及正式审计外层均wait0；独立核43TRX/20574唯一execution及4598历史文件，报告SHA `90c61a45b181f13c60438bd242b4d152d96ff9536288589753234c0e01c8f0a8`。审计后字节绑定和before实际通过，保护1121本片原项；随后仅四文档及唯一TXT交接变化，最终after以`handoff-v1/post-handoff-check.json`实际结果为准。未改live，详§19.41。
- **Working tree at validation（V2绑定历史）：2026-10-12未提交V2认证绑定纯核，`outputs/m6-auth-v2-20261012/final-v1/summary.json` passed/exit0。** 四Rebuild零警告错误、16TRX双配置各5019PASS/0FAIL/0SKIP（Transport3460，旧4944+新增75），371项源码冻结SHA `2bb62b17e480af86fe8185c8815c8b68d915dfd6b8956a695333139295e69dd3`，summary SHA `498678a0322bd577302432e08abe477297d6a78a2688f26f74f10b347782e2c0`。原完整冻结外层真实0；新68项正式护栏自测及正式独立审计外层真实0，后者独立核45TRX/20175唯一execution，报告SHA `f1c7158d15aac4da28b24f1a251dbd411ab5c21b102f39d2c1386be84b3bdd62`。审计字节绑定及handoff before已独立通过；随后仅四文档及唯一TXT交接变更，最终after以`handoff-v2/post-handoff-check.json`实际结果为准。真实认证仍V1，纯核不等于live，详§19.40。
- **Working tree at validation（Offer/Accept历史）：2026-10-10未提交新版专用Offer/Accept纯codec；`outputs/m6-catalog-agreement-20261010/final-v1/summary.json` passed/exit0，完整冻结及正式独立审计外层真实exit0已回收。** 四Rebuild零警告错误，六dotnet命令rc0/wait，16TRX Debug/Release各4944PASS/0FAIL/0SKIP（Transport3385、新108）。367源前后/12边界与28文档保持，源清单SHA `567806036322f92f83ae906958a6e29a3ae81279fbfe967d8b478af6bae690aa`，摘要SHA `9b95ef8c41d9910dd406bf7c18e94a444ea3de67ddf7417f168e718b1ca33b39`。正式审计独立43TRX/19958唯一execution，报告SHA `015875406168e134466c2de334ec97a0eca41badd78f8a18d58d1e947ce0b334`；旧4836稳定身份+新108精确对应，八变异真实断言/恢复、全部历史及根脚本/旧ZIP绑定。B政策已定但live拒旧/V2认证未实施；本HANDOFF/ADR083/本日日志为审计后文档例外，见§19.39。
- **Working tree at validation（外部首字节历史）：2026-10-10未提交外部首字节原锚定入口；`outputs/m6-observed-entry-20261010/final-v2/summary.json` passed/exit0，外层实际exit0已回收，正式独立审计main/外层实际exit0。** 六dotnet命令rc0/直接wait、四Rebuild零警告错误、16TRX Debug/Release各4836 PASS/0 FAIL/0 SKIP（Transport3277，新入口48）；365项前后/12边界相同，清单SHA `aebc6475c4e608a265a8e76124843ec2ad32f48f2d16dfef980820bb158ce3f6`，摘要SHA `f5971eb33a13c6193ed5034cf1bbe7fb9794f86c384237dd1c881e213b2faabc`。审计报告SHA `f544a2bd6e5db12dba1d3c9e53292315f7e17c9f8236f8b2a757c1a5c6e59302`，独立核54TRX/24707唯一execution；源码/文档完整重枚举。原final-v1验证器错名exit2、原变异分类器exit2均原样保留，不洗成通过。未接live/新版双机，HANDOFF/ADR082/本日日志为审计后文档例外，见§19.38。
- **Working tree at validation（Host移交历史）：2026-10-09未提交私有Host读槽一次移交；`outputs/m6-host-read-handoff-20261009/final-v1/summary.json`落盘passed/exit0，2026-10-10正式独立审计实际exit0。** 四Rebuild零警告错误、六直接dotnet命令rc0/wait记录、16原TRX Debug/Release各4788 PASS/0 FAIL/0 SKIP（新移交46、旧owner54、Transport3229）；364项前后/12边界相同，清单SHA `fc3f584e4851237aef2e836bc593e9199a6c324ce979fd40b74453962651cadc`，摘要SHA `bcef134b56ef204bdf8c005e43d781ec7f9fd6d68b68ee2493139d119eb76a61`。正式审计报告SHA `9d0f754170577361fce15f76a1d3f706bbf775c8263f3e28964486dcc0afa82d`；原外层final后台句柄不可回收，不捏造本轮观察到其进程rc。clean18红→46绿、八变异恢复、400回归均从原证据核实。未接live/新版双机，文档更新为冻结后例外，见§19.37。
- **Working tree at validation（Host单槽历史）：2026-10-09未提交Host审批→holding单读槽所有权；`outputs/m6-host-read-owner-20261009/final-v1/summary.json`及独立审计passed。** 四Rebuild零警告错误、六直接命令rc0/逐个wait、16TRX Debug/Release各4742 PASS/0 FAIL/0 SKIP（Host专项54，Transport3183）。363项前后及12边界一致，清单SHA `665d2fdc6316f5dc8f0216d1b493cd56c671d34ed9bd128cc89930cf6bf14eec`，摘要SHA `3e74190f1d5945c81ac1cf6318783a9ccba1c00bd83bf1081f109b53e6729864`。原失败、同测试红绿、helper红绿及四变异恢复均独立核对；未接live帧/双机，后续文档为冻结后例外，见§19.36。
- **Working tree at validation（组合历史）：2026-10-09未提交独立单帧预算组合；`outputs/m6-frame-attempt-20261009/final-v1/summary.json`及独立审计passed。** 四Rebuild零警告错误、六直接命令rc0/逐个wait、16TRX Debug/Release各4688 PASS/0 FAIL/0 SKIP（组合66，Transport3129）。362项前后及12边界一致，清单SHA `d43e390e75616bd7608a2a73ceba6f26613b3824339b6bc497941cf8472f7660`，摘要SHA `12dd3fc21cd0ff411fb27960858f7a5fc7a73b64458f457dc58f8b2d480246d2`。未接live帧/双机，后续文档记账为例外，见§19.35。
- **Working tree at validation（预算历史）：2026-10-09未提交独立锚定预算；`outputs/m6-frame-budget-20261009/final-v1/summary.json`及独立审计passed。** 四Rebuild零警告错误、六直接命令rc0/逐个wait、16TRX Debug/Release各4622 PASS/0 FAIL/0 SKIP（预算101，Transport3063）。360项前后及12边界一致，清单SHA `105496df558731d53beda71fd7cfd00ffc5d051b15cb760423cbc0422923d449`，摘要SHA `c7c755481528cb19e32ac49835e8b0e63dd6461a9a3aef75f6c947e73e27f1ff`。未接live/双机，后续文档及下一组合片不在该冻结内，见§19.34。
- **Working tree at validation（单帧接续历史）：2026-10-09未提交单帧接续；`outputs/m6-frame-continuation-20261009/final-v1/summary.json`与独立审计passed。** 四Rebuild零警告错误、六命令rc0、16TRX Debug/Release各4521 PASS/0 FAIL/0 SKIP（单帧110、Transport2962）。358项前后及12边界一致，清单SHA `53f1814023469d3d3e6fcddeeed0b2c9b0fd6d9e86d5b6758f8aa4d473ef69b3`，摘要SHA `30defbb5fd6597005fc1c884e34a79ac6a90677432418b6fb360ae5112f51355`。未接live/双机，后续文档例外见§19.33。
- **Working tree at validation（首字节历史）：2026-10-09未提交首字节所有权；`outputs/m6-read-ahead-20261009/final-v1/summary.json`与独立审计passed。** 四Rebuild零警告错误、六命令rc0、16TRX Debug/Release各4411 PASS/0 FAIL/0 SKIP（首字节39，Transport2852，其余分项不变）。356项前后及12边界一致，清单SHA `7bb79707ebccfaae914e5d0611f336d07802ebe292533e26e43d9a7122136e09`，摘要SHA `6e64943e5452707fe4785007362a80d6cebcb62e5aa2ad9d37748cc14b583d10`。未接live、不含新版双机；后续HANDOFF/ADR/memory是文档例外，详§19.32。
- **Working tree at validation（元数据历史）：2026-10-09未提交目录过滤补测及host_status纯合同；`outputs/m6-metadata-20261009/final-v1/summary.json` passed/exit0，独立`independent-audit.json` passed。** 四完整Rebuild零警告错误、六命令rc0、16TRX双配置各**4372 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2813/Acceptance503/Capture243/Sessions244；元数据516、目录84）。354项前后及12边界SHA一致`61e91e77b0b6857142c90004d7b62bd3eb51da344df8c89fd80b166a1dd49458`，摘要SHA`c7376e1ebf3af24edcc397d2d236e974c5ed89fe9eaff286a9f1918b79918498`。只读实机仍一个OrayIddDriver设备；不等同物理多屏/双机GUI。冻结后HANDOFF/ADR/memory为文档例外；下一首字节切片如改源码须另行验证，不能沿用本冻结声称新代码已测，详§19.31。
- **Working tree at validation（M6目录历史）：2026-10-09未提交工作树新增M6本机只读目录；`outputs/m6-displays-20261009/final-v1/summary.json` status=passed/exit0。** 四Rebuild零警告错误、六命令rc0、16TRX逐项/Counters双配置各**3852 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance503/Capture239/Sessions244）。352项前后及12边界SHA一致`02d3371bc2366c9f23f1772857c1fa077c6685cd8a07749f611453e28bc2a87d`，摘要SHA`21c88724c953e7701a3f090897dc42042ec17f9a08b82e04e1b9b5a0b2118f0f`，独立核对`independent-audit.json` passed。真实只读API仅观察到一个OrayIddDriver设备/index4/1920×1080，不是物理多屏/混合DPI或GUI审批验收。随后HANDOFF/ADR/memory更新属于文档例外，未再改源码；未出新包、不提交推送，详§19.30。
- **Working tree at validation（M5候选历史）：2026-10-08未提交M5完整手动画质产品工作树加TransportHost窄Stop/accept修复，M5独立冻结`outputs/m5-host-video/final-m5-quality-20261008-v3/summary.json`（status=passed/exit0）。** 四完整Rebuild零警告错误、六命令rc0、16份TRX逐UnitTestResult/Counters双配置各**3772 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance503/Capture159/Sessions244）；350项实际源码含未跟踪App/Tests与HANDOFF在冻结前后及12边界SHA `dbff72f66cbbdabe6ea44c5d260df3ac3f8d079fa8b301b471d99c8ffd0329d5`一致，docs/memory与Git status前后原字节一致。摘要SHA `fc57ba7fb50d8ac65b8658c043e7e86d297da3ab98a3081b98c901554841455c`；此后HANDOFF与docs/memory编辑属于冻结后文档例外，不宣称当前350项仍全同SHA。仅六直接子进程wait，未审全机。v1 Release、v2 Debug各一Transport停机真红历史保留，不把失败轮写成绿；详§19.29。`Last code commit`仍`b3e1273`，新App/Core/Capture/Sessions/Transport源码及测试尚未提交，新哈希包未实机测试。
- M3.1 记录（历史）：Last code commit = `2dee00c`（pre-auth 外层信封 + 停机报告 +
  B15/B16/B18/B19/B20 测试补强 + 验收器同步）；601 PASS 验证后未再动代码。
- **注意：M3.1 起至 M4 阶段 3，每一轮都改动过 `src` / `tests`**——M3 两机验收的旧物料
  （zip `f81d194c…`，由 `1d5ffc8` 后工作树打出，harness / transport 哈希两端逐字符一致）
  **早已是历史版本**；如需重跑两机验收，物料必须按最新代码重打（§18.4 A 节 / §18.5 D 节）。
- **两机 lab 环境均已就绪，且第 24 步两机验收已跑完（PASS，2026-09-21）**：
  A（本机）= `192.168.1.10`、B = `192.168.1.20`，UDP 45872 + TCP 45873 入站放行；
  A 侧四场景 4/4 PASS（runId `8a7e3d03`）、B 侧四条连接行与五条汇总约束全部对上
  （runId `0e7f03d0`）。**收尾已完成：两机 lab 均已撤销还原**——B 机 2026-09-21 11:13Z
  （用户点窗口按钮）、A 机 11:21Z / 本地 19:21（管理员直跑同物料 `--headless lab-undo`，
  runId `037fd836`）；两机撤销后 `--headless info` 均 `qualifiedNic=(无)`。判定明细与
  `INVALID_RUN` 口径见第 15 节第 24 步记录。
- **M2.1 code 状态：Implementation complete；Two-machine manual DoD：PASS**
  （2026-09-20 17:30–18:18 两台实机跑完 20 步，20/20 通过，见第 9 节）
- **是否满足完整 M2 DoD：是**（两机手工验收已回填）
- **M3：完成**——24 步全部完成，第 24 步两机验收（真实 TLS + pinning）**PASS**（2026-09-21）。
  判定以逐条连接证据为准（五约束 + 四元组配对）；被控端结局字段 `INVALID_RUN` 是收尾机制
  的机械产物、**不是失败**（第 15 节第 24 步记录有专述）。
  `dotnet build` 0 警告 0 错误，`dotnet test` **574 PASS / 0 FAIL**（验收轮未改代码）。
  逐步明细、实测数据与禁止回访项见**第 15、16 节**——以第 15 节为准，本节可能滞后。
- **验收器是 WPF 窗口程序（`WinExe`），双击 `LanRemote.Acceptance.exe` 就是一个窗口**，
  不需要任何脚本；2026-09-24已移除准备A/B及撤销入口，网络检查只读；
  同一个 exe 带 `--headless` 仍可运行 info/host/client，旧改网动词一律拒绝。
  曾短暂采用「控制台 exe + `START.cmd`」的形态，被用户连纠三次后废弃——
  **别改回去**，理由与坑见第 15 节「M3 验收器的形态教训」。
- **M3时代历史注意（仅限当时版本，不适用于当前M5未提交产品工作树）：不要拿当时的 `LanRemote.App` 验收 M3**——它虽引用 `LanRemote.Transport`，但当时一行都没调用，
  打当时的包只能重证 discovery。现在App已接入Transport/视频，但仍须另做当前产品双机实屏验收。
- **远端仓库已上线（2026-09-21）：`https://github.com/Nuyoah-sir/LanRemote.git`（public，
  用户手动建库）**——完成**首次全量推送**：`main` 与本地一致（52 提交 / 693 对象 / 726 KiB，
  远端 `refs/heads/main` 与本地 HEAD 逐字符相同）。推送走 git+HTTPS（GCM 缓存凭据，
  `gh` 未安装也不需要）；此后记账提交按「关于 git 记账方式」同步推送。

### 关于 git 记账方式

**2026-10-12新增持续授权：用户明确“可提交可推送。然后继续开发，每完成一片都可以推送提交，这样有利于查找、恢复、回档”。** 先将当前已验证的M5产品和M6独立合同累计工作树建立检查点，随后每片验证完成新建提交并正常推送，无须重复询问。此前各节“不提交”保留为当时事实，此后以本授权为准。不强推、不amend/改写历史、不跳hooks/签名、不改Git配置，不将当前累计提交伪装成过去各片分别提交。

HANDOFF 不写 HEAD hash（写完立刻过期的自引用）。固定使用：
`Last code commit`（最后一次代码/测试提交）+ `Working tree at validation`。
允许 HEAD 比 Last code commit 新（之后会有单独的文档提交）。
远端 `origin` 已配（GitHub，public）：每轮记账提交后 `git push origin main` 同步。
**两个已实测的坑**：① 链路间歇性抖动（push 挂到超时 / 偶见 schannel 握手失败）→ **重试即过**；
② **helper-selector 陷阱**：`git-credential-helper-selector` 每次被 git 当 helper 调用都会**弹 GUI**
（源码级：无「已选过即静默委托」分支；无桌面会话挂起；连 `--help` 都弹并写配置），选「`<no helper>` + Always」
会把它写成 `credential.helper = <空>`（清链）——2026-09-21 19:57 本机全局就是这样被写坏的（推送 `exit 128`）。
**修复（2026-09-21，全局 + repo 双层）**：`credential.helperselector.selected = manager` + 链「空值 + `manager`」
（空值重置 system 级 `helper-selector`）。机器级验证：从 `/tmp`（无 repo 配置参与）`git credential fill` rc=0、
`GIT_TRACE` 只见 `git-credential-manager`。**不要改回、不要裸跑那个 selector。**
push 仍带 `GIT_TERMINAL_PROMPT=0` + 关 GCM 交互（`credential.interactive=false`/`guiPrompt=false`）。

## 1.5 M2.1 — Discovery Final Fix（本轮修复明细）

源码审计 M2 之后发现 1 个真实功能 bug + 4 个协议/可靠性问题，本轮全部修复。
**没有**进入 M3：**没有** TCP、TLS、SslStream、Auth、HMAC、Video、Input、Session 的任何代码。

| # | 问题 | 状态 |
|---|---|---|
| 1 | probe unicast 回应发错端口（本轮阻断项） | ✅ 已修复 + 回归测试 |
| 2 | 未显式指定组播出口网卡（多网卡会走默认路由） | ✅ 已修复 + 实测验证 |
| 3 | capabilities 校验过松（空项静默跳过、上限按去重后算、控制字符放行） | ✅ 已修复 + 回归测试 |
| 4 | capability 测试语义错误 | ✅ 已重写 |
| 5 | discovery 启动失败的状态文本被 config 文案覆盖 | ✅ 已修复 |
| 6 | `StartAsync` 创建 socket 失败时 linked CTS 未 Dispose | ✅ 已修复 |
| 7 | probe 回应成功时**没有任何日志**，两机验收无法判定链路是否真的通 | ✅ 已补一行 Debug 日志（见下） |

### 1.5.6 补充：probe 回应日志（为两机验收的可判定性而加）

`ReplyToProbeAsync` 原先只在「拒绝回应」和「发送失败」时写日志，**成功回应时没有日志**，
导致验收第 18 步「B 应能正常响应」无法从日志判定。已补一条 Debug：

```text
已回应来自 {ProbeAddress} 的 probe：unicast → {ReplyTarget}（不使用源端口 {SourcePort}）。
```

它同时记录「被丢弃的源端口」与「实际回应目标」，现场就能确认回应没有再打到对方的随机临时端口上。
只记地址与端口，不记报文内容、不记任何秘密。**纯日志，未改动任何逻辑**（重跑后仍是 408 passed）。

### 1.5.1 probe 回应端口（阻断项）

- **probe 的来源端口示例**：`192.168.1.20:53742`
  —— 发送 socket 是 `Bind(binding.Address, 0)`，端口由系统分配的随机临时端口。
- **旧行为**：`ReplyToProbeAsync` 把 `remote` 整个当回应目标 → 发给 `192.168.1.20:53742`。
  那个临时端口上<b>没有任何人在监听</b>，回应必然丢弃。
  **症状**：点了「刷新」，对方就是收不到、列表就是不出现；announce 周期（2 s）未必能救回来，
  因为定向广播 probe 的使用场景恰恰是「对方收不到组播」。
- **修复后最终目标端口**：`192.168.1.20:45872` —— 即 `remote.Address : DiscoveryConstants.Port`。
  LanRemote 的 discovery receiver 永远监听 UDP 45872，端口是常量，只有地址取自报文来源。
- **实现**：新增 internal 纯函数 `DiscoveryReplyTarget.ForProbe(IPEndPoint)`；
  `ReplyToProbeAsync` 改为 `DiscoveryReplyTarget.ForProbe(remote)`，
  代码里已<b>禁止</b>再把 `remote` 直接当目标传下去。
  该类型是 internal，测试工程通过 `InternalsVisibleTo("LanRemote.Protocol.Tests")` 访问，
  **没有**为测试新增公共 API。

### 1.5.2 组播出口网卡（IP_MULTICAST_IF）

- **最终设置方法**：对每个 sender，在保留 `Bind(binding.Address, 0)` / `Broadcast=true` /
  `MulticastTimeToLive=1` 的基础上，追加
  `SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, optionValue)`。
- **optionValue 的形式**：**接口 IPv4 地址的 4 字节网络序**，
  由 `MulticastInterfaceOption.ForInterface(binding.Address)` 产出（内部就是 `IPAddress.GetAddressBytes()`，
  已是网络序，**不需要**任何额外字节序转换）。
- **为什么必须显式设置**：`Bind` 只决定 unicast 源地址，组播从哪张卡出去由 `IP_MULTICAST_IF` 决定。
  不设置 → 全部退化成系统默认组播路由；Ethernet + Wi-Fi 各有私有 IPv4 时，
  另一张卡所在子网永远收不到 announce。现在做到「sender for binding A → multicast interface A」。
- **本机实测证据（.NET 10 / Windows，设置后立即 `GetSocketOption` 回读，不是猜测）**：

  | 写法 | 结果 |
  |---|---|
  | `byte[4]` = 接口 IPv4 地址（`GetAddressBytes()`） | ✅ OK，回读 = 原地址（如 172.100.166.220） |
  | `int` = `IPAddress.HostToNetworkOrder(interfaceIndex)` | ✅ OK，回读 = `0.0.0.6` 形式（索引 6） |
  | `int` = `IPAddress.HostToNetworkOrder((int)addr.Address)` | ❌ `SocketException: 在其上下文中，该请求的地址无效` |
  | `int` = 裸接口索引（不转换字节序） | ❌ `SocketException: 在其上下文中，该请求的地址无效` |

  最终采用第一种（地址形式）。测试 `MulticastInterface_CanBeAppliedToRealSocket`
  会在 Windows 上真实建 socket 并断言设置不抛异常、回读值还原为设置的地址。

### 1.5.3 capabilities 语义（三处收紧）

| 语义 | 规则 |
|---|---|
| **raw count 上限** | **去重之前**先判：`raw.Count > 16` → 拒绝。不再按去重后的 `HashSet.Count` 判断。否则 `["view" × 100]` 去重后只剩 1 项，可以无限绕过「最多 16 项」。 |
| **空白 capability** | `null` / `""` / `" "` / `"   "` / `"\t"` / `"\r"` → **一律拒绝**（`return false`），不再 `continue` 静默跳过。对端声称自己有一个「空白能力」本身就是畸形报文。 |
| **控制字符** | 含 `\n` `\r` `\t` 等控制字符 → **拒绝**。原因：第一次发现设备时 capability 会进日志，放行 `\n`/`\r` 等于让局域网报文伪造日志行。<br>**判定发生在 `Trim()` 之前**——`\n`/`\r`/`\t` 本身也是空白，先 Trim 会把 `"view\n"` 洗成 `"view"` 从而放过（这条是写测试时才暴露的真实漏洞，已修）。 |
| **保持允许** | `view` / `control` / `future-capability`（unknown capability 仍允许）；合法重复项去重；首尾普通空格允许 Trim（`" control "` → `control`）；单项上限仍为 32 字符；`null` 列表与空列表仍视为「无能力」并接受。 |

### 1.5.4 discovery 启动失败的状态文本

- **已修复**。`StartDiscoveryAsync` 改为返回 `bool`；失败时由它**自己**写入
  `本机身份已加载，但局域网发现启动失败。`；
  `LoadAsync` **只有**在返回 `true` 时才写「已从磁盘加载配置与本机身份。」/「未发现配置文件…」。
- 旧行为：无论成功失败，`LoadAsync` 都会无条件覆盖，把「端口被占用」这类真实网络错误
  伪装成「加载成功」。
- 身份**仍然不会**因为 discovery 失败而重置。

### 1.5.5 CTS 失败路径

- **已修复**。`StartAsync` 中 socket 创建失败时：`CloseSocketsCore()` → `_cts = null` →
  **`cts.Dispose()`** → `throw`。
- 不会 double-dispose：失败路径上一个后台循环都没起来，且 `_cts` 已置空，
  `StopAsync` 会直接返回，碰不到这个 CTS。

## 2. M2 做了什么

两台 LanRemote 只要位于允许的同一 IPv4 私有子网，即可自动互相发现并在 MainWindow 显示真实设备。

本轮**没有**：TCP listener、SslStream、TLS 校验、channel_hello、AuthChallenge、HMAC、
访问密钥网络认证、SessionToken、视频、抓屏、JPEG、SendInput、SessionWindow、文件传输、
剪贴板同步、音频、公网穿透、UPnP、NAT、云服务器。**也没有**任何防火墙自动修改（属于 M10）。

设备列表里的「连接」按钮保持 disabled，提示「连接功能将在 M3/M4 实现」。

## 3. NetworkInterfaceSelector 最终筛选规则

`src/LanRemote.Discovery/Networking/NetworkInterfaceSelector.cs`

对 `NetworkInterface.GetAllNetworkInterfaces()` 的每个网卡，逐个检查：

| 条件 | 通过要求 |
|---|---|
| 运行状态 | `OperationalStatus == Up` |
| 网卡类型 | `Ethernet` / `FastEthernetT` / `FastEthernetFx` / `GigabitEthernet` / `Wireless80211`（显式排除 Loopback、Tunnel、Ppp、Unknown、Wwanpp 等） |
| 虚拟/VPN 过滤 | 名称或描述不含虚拟关键词（见下） |
| IPv4 地址 | `AddressFamily == InterNetwork` 且属于 RFC1918 |
| 子网掩码 | 非空、`Ipv4Math.IsUsableMask` 为真（连续前缀掩码，排除 0.0.0.0 与 255.255.255.255） |

虚拟/VPN 关键词（`VirtualAdapterFilter`，大小写不敏感）：
`virtual` `hyper-v` `vethernet` `vmware` `virtualbox` `wireguard` `wintun` `tailscale` `zerotier` `tap` `tunnel` `vpn`。
刻意**不**使用 `microsoft` / `intel` / `realtek` 这类会出现在真实网卡名称里的词（会误杀）。

一张 NIC 有多个合格 IPv4 时，每个地址各自形成一个 `NetworkBinding`。

`NetworkBinding` 至少包含：本机 IPv4、subnet mask、directed broadcast（`address | ~mask`，不硬编码 `.255`）、接口身份（`InterfaceId` / `InterfaceName` / `InterfaceType` / `InterfaceIndex`）。

为可测试性引入的极小 OS 抽象：`INetworkInterfaceSource` + `SystemNetworkInterfaceSource` + `NetworkInterfaceSnapshot`，
这样单元测试不必真的修改 Windows 网卡。

## 4. ISubnetPolicy 实现位置

`src/LanRemote.Discovery/Networking/SubnetPolicy.cs`，实现既有接口
`LanRemote.Core.Abstractions.ISubnetPolicy`（**未修改其语义**）。

判定顺序：

1. local / remote 都必须是 IPv4；
2. remote 必须 RFC1918；
3. 按 `localAddress` 找到**完全匹配**的本机 `NetworkBinding`，找不到 → `false`；
4. 用该绑定的**真实掩码**比较网络号（`local & mask == remote & mask`）；
5. 相等 → `true`。

「两者都是 RFC1918」**不**等于同子网（10.x 与 192.168.x 都是私有地址但不同网络）。
支持 /8、/12、/16、/20、/23、/24、/25 等任意掩码，不假定 /24。
禁止字符串前缀或「前三段相同」这类近似判断。

## 5. Discovery socket 结构

`src/LanRemote.Discovery/LanDiscoveryService.cs`

- **receiver**：`AddressFamily.InterNetwork / Dgram / Udp`。
  在 `Bind` **之前**依次设置 `ExclusiveAddressUse = false` 与 `ReuseAddress = true`（Windows 顺序敏感），
  然后 `Bind(0.0.0.0:45872)`，最后对每个合格 binding 加入组播 membership。
  接收缓冲区 `2049` 字节（协议上限 2048 + 1）。
- **sender**：每个合格 binding 一个 socket，`ReuseAddress = true` → `Bind(binding.Address:0)` →
  `Broadcast = true` → `MulticastTimeToLive = 1` →
  **`MulticastInterface = 该 binding 的 IPv4 地址（网络序 4 字节）`**（M2.1 新增）。
  最后这一步是决定性的：Bind 只管 unicast 源地址，组播出口必须靠 `IP_MULTICAST_IF` 指定，
  否则多网卡时会全部走系统默认组播路由（详见 §1.5.2，含本机实测证据）。
- **无合格网卡时不开任何 socket**（既不拿不到可信来源，也不该白占端口）。
- 三个后台循环：`ReceiveLoop` / `AnnounceLoop` / `CleanupLoop`，全部受同一个 `CancellationTokenSource` 控制；
  `StopAsync` 会 cancel、关闭 socket、`await` 三个循环、complete channel。
- `StartAsync` / `StopAsync` 均幂等。

## 6. Multicast group 与 probe 行为

| 项 | 值 |
|---|---|
| 组播地址 | `239.255.77.77` |
| 端口 | UDP `45872` |
| TTL | `1` |
| announce 周期 | 2000 ms |
| 缓存 TTL | 7000 ms |
| 清理周期 | 1000 ms |
| 协议版本 / 魔数 | `1` / `LANREMOTE` |
| 单报文上限 | 2048 字节 |

- **announce**：每 2 秒（含启动时立即一次）向 `239.255.77.77:45872` 发送（每个 binding 各一次）。
- **probe**：组播 probe **+ 每张合格网卡的 directed broadcast probe**（例如 `192.168.1.255:45872`）。
  启动后自动发一次；UI「刷新」按钮也只发 probe。
- **收到 probe**：来源先过 RFC1918 + 同子网；若 `AllowDiscovery == true`，
  用 **unicast** 回一个正常 announcement（对方之所以发 directed broadcast，往往就是收不到组播）。
  **回应目标 = `remote.Address : 45872`**（M2.1 修复）：
  probe 的来源端口是对方的随机临时端口（如 `192.168.1.20:53742`），绝不能作为回应目标端口；
  回应固定打到 `192.168.1.20:45872`。详见 §1.5.1。
- 协议端口护栏：`DiscoveryConstants.ExpectedTransportPort == TransportConstants.Port`（测试守护，防漂移）。

## 7. AllowDiscovery=false 的准确语义

| 行为 | AllowDiscovery = true | AllowDiscovery = false |
|---|---|---|
| 主动 announce | ✅ | ❌ |
| 回应别人的 probe | ✅ | ❌ |
| 接收别人的 announcement | ✅ | ✅（列表继续显示其他设备） |
| 主动 ProbeAsync 扫描 | ✅ | ✅（「允许被发现」不是「关闭扫描」） |

实现上：`DiscoveryRuntimeState` 保存不可变快照，`AllowDiscovery` 变化时
`UpdateFromConfig` 更新快照，下一次 announce / probe 响应立即使用新值——
**不重启 UDP 服务、不重读 config.json、不 stop/start socket**。

远端在 TTL（约 7 秒）后把本机移出列表；重新开启后最多约 2 秒再次出现。

## 8. WatchAsync 的 upsert-only 语义

`WatchAsync` 的返回类型未改。它只推送「在线设备的插入或更新」，
**不会**发送任何「Removed」假设备。UI 必须自己按 `DiscoveredDevice.LastSeen` 做 TTL prune。
这是当前 Core API 的既定语义，M2 不推翻（另见 ADR-023）。

- **cache**：`DiscoveryDeviceCache`，key = `DeviceId`（一台物理设备多 NIC 也只在 UI 出现一条；
  同 DeviceId 换 IP 时 v1 采用最新有效 endpoint），
  **容量上限 256**，满时先清过期再淘汰最旧，绝不无界。
- **更新队列**：`Channel<DiscoveredDevice>`，容量 **512**，`FullMode = DropOldest`。
- **离线 TTL**：内部缓存 7 秒；清理循环每 1 秒检查一次。
- **UI TTL removal**：`MainViewModel` 起一个 1 秒的 `PeriodicTimer` 清理循环，
  在 UI 线程从 `ObservableCollection` 删除 `LastSeen <= now - 7s` 的条目；
  窗口/应用退出时该循环随 `CancellationTokenSource` 停止，不用永久 `DispatcherTimer`。

## 9. 手工测试

### Two-machine manual discovery: **PASS**（2026-09-20 17:30–18:18，20/20）

> **这一项已从 NOT RUN 变为 PASS。** 下面第 9.1 节是完整的回填记录
> （时间线 + 20 步逐条结论 + 两台机器的证据）。
> M2.1 修的两件事（probe 回应端口、组播出口网卡）都在这次实测中拿到真实链路证据。

（保留原始说明：）单元测试无法替代两机验收——尤其本轮修的两件事
（probe 回应端口、组播出口网卡）本身就是「只有真实多网卡/多机环境才暴露」的问题，
纯单测只能覆盖端口计算与选项值构造，覆盖不了真实收发路径。

原因（两条，各自都足够）：

1. **只有一台物理测试机**；
2. **本机以太网的 `172.100.166.220` 不属于 RFC1918**
   （172.16.0.0/12 只覆盖 172.16–172.31）。两个「本地连接*」为「媒体已断开连接」，
   只有 APIPA `169.254.x.x`。
   > 2026-09-20 17:10 复测更正：WLAN 此时已连接并拿到 `10.65.156.134/24`（Dhcp，**是** RFC1918
   > 私有地址），所以本机现在**存在**合格网卡。但电脑 B 不在 `10.65.156.x` 网段，
   > 两机仍然互不可见 —— 结论不变：**必须**靠 `set-lab-ip.ps1` 造出双方共享的 `192.168.1.0/24`。

### 9.1 两机验收回填（2026-09-20，PASS）

#### 环境

| | 电脑 A（本机，由施工环境操作） | 电脑 B（由用户操作） |
|---|---|---|
| 机器名 | `DESKTOP-D132BMD` | `DESKTOP-CU2263D` |
| 设备码 | `M5WC-14GX` | `3ERD-R74V` |
| 证书指纹前缀 | `89A5C10E` | `6755838E` |
| 物理网卡 | Realtek PCIe GbE | Realtek Gaming 2.5GbE |
| 原有 IPv4 | `172.100.166.220`（**非** RFC1918） | `172.100.166.65`（**非** RFC1918） |
| lab 地址 | `192.168.1.10/24` | `192.168.1.20/24` |
| 解压目录 | `C:\DIYTools\LanRemote-0.1.0-m2-win-x64` | `D:\DIYTool\LanRemote-0.1.0-m2-win-x64` |

两台都在同一条物理网线上；`172.100.166.x` 不是私有网段，靠 `set-lab-ip.ps1` 造出共享的 `192.168.1.0/24`。

#### 关键时间线（A 机 `lanremote-20260920.log` 原文摘录）

```
17:30:42.424 [Information] 局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10
17:30:42.444 [Debug     ] 丢弃发现报文：本机自公告（累计=1）          ← 第 5 条：不列自己
17:51:03.892 [Debug     ] 已回应来自 192.168.1.20 的 probe：unicast → 192.168.1.20:45872（不使用源端口 54670）。
17:51:03.896 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。   ← 第 3 条
17:57:33.435 [Information] 设备离线：DESKTOP-CU2263D（3ERD-R74V）192.168.1.20。   ← 第 8 条（B 退出后 ~7 s）
18:01:15.999 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。   ← 第 10 条
18:02:13.438 [Information] 设备离线：DESKTOP-CU2263D（3ERD-R74V）192.168.1.20。   ← 第 12 条（B 关广播后 TTL 到期）
18:04:07.620 [Debug     ] 已发送 probe：组播 + 1 个定向广播。        ← 第 17 条（A 点刷新）
18:04:13.550 [Debug     ] 已回应来自 192.168.1.20 的 probe：unicast → 192.168.1.20:45872（不使用源端口 58869）。  ← 第 18 条
18:04:23.990 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。   ← 第 16 条
```

#### 20 步逐条结论

| # | 验收项 | 结果 | 证据 |
|---|---|---|---|
| 1 | A 启动 | ✅ | `17:30:42.424 局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10` |
| 2 | B 启动 | ✅ | B 侧日志 `18:01:16.419 本机身份就绪…DESKTOP-CU2263D`；`discovery started=2` |
| 3 | A 看到 B | ✅ | `17:51:03.896` 发现设备（B 启动后约 5 s） |
| 4 | B 看到 A | ✅ | 用户在 B 机界面确认；B 侧 `device discovered=2` |
| 5 | 自己不出现在自己列表 | ✅ | `丢弃发现报文：本机自公告（累计=1024）` |
| 6 | 20 s 内无重复/抖动 | ✅ | 40 s 窗口内「发现设备」仅 1 次 |
| 7 | B 退出 | ✅ | 用户关闭 B 机 LanRemote |
| 8 | A 约 7~9 s 后移除 B | ✅ | `17:57:33.435` 设备离线（关闭后 ≈7 s，符合 TTL 7000 ms + 清理 1000 ms） |
| 9 | B 重启 | ✅ | `18:01:1x` |
| 10 | B 再次出现 | ✅ | `18:01:15.999` |
| 11 | B 关闭 `AllowDiscovery` | ✅ | 用户在 B 机取消勾选「允许被发现」 |
| 12 | A 在 TTL 后移除 B | ✅ | `18:02:13.438` 设备离线 |
| 13 | B **仍**能看到 A | ✅ | 用户在 B 机界面确认 |
| 14 | B 点刷新**仍**能 probe 到 A | ✅ | 用户在 B 机界面确认 |
| 15 | B 再打开 `AllowDiscovery` | ✅ | `18:06:33` |
| 16 | A 再次发现 B | ✅ | `18:04:23.990`；此后约 3 分钟 **0 次**「设备离线」（TTL 仅 7 s，收不到广播早该掉→证明广播已恢复且稳定） |
| 17 | A 点刷新 | ✅ | `18:04:07.620 已发送 probe：组播 + 1 个定向广播` |
| 18 | B 正常响应 | ✅ | `18:04:13.550` / `18:04:23.277` 收到 `192.168.1.20` 的 probe 并回应 |
| 19 | 两边无崩溃 | ✅ | A：pid 43572 从 `17:30:41` 连续运行至 `18:18` 被主动停止，无异常退出；B：全程在 A 列表可见且可响应 |
| 20 | 日志无 Access Key | ✅ | A、B 两侧 `check-logs.ps1` 均 `matches : 0` + `VERDICT: PASS` |

**M2.1 两个头条修复的真实链路证据：**

- **§1.5.1 probe 回应端口**：三次独立出现「不使用源端口」——`50193`（16:10 那轮）、
  `54670`、`58869`。源端口都是随机临时端口，若按 v1 回源端口则对端必然收不到。
- **§1.5.2 组播出口网卡**：B 机同时挂了 5 张带私有地址的网卡（以太网 `192.168.1.20`、
  以太网 3 `172.19.83.237`、VMnet1 `192.168.11.1`、VMnet8 `192.168.119.1`、
  本地连接* 10 `192.168.137.1`），A 只在 `192.168.1.20` 上收到 B，
  **没有出现跨网卡混淆**，也没有 `AddressNotAvailable`。

#### 未覆盖 / 需注意（如实标注，不算通过）

- **「Ethernet + Wi-Fi 同一 subnet」组合未测** —— 两台这次都是有线以太网。
- **第 19 条 B 侧**：用户未逐字回复「无崩溃」，结论由「全程在 A 列表可见、点刷新可响应」佐证，
  不是直接确认。若要严格化，需补一次 B 侧进程存活确认。
- **验收后两台均已回滚**：`-Undo` 均 `EXIT=0`，A 恢复 `172.100.166.220/24 Dhcp`
  （网关 `172.100.166.254`、ping 通、无 lab 地址残留），B 恢复 `172.100.166.65/24 Dhcp`。

#### 本轮实测暴露的验收脚本缺陷（5 个，全部已修并提交）

| # | 缺陷 | 后果 | 修复 |
|---|---|---|---|
| 1 | `set-lab-ip` v1 假设「DHCP 地址 + 附加静态地址」可共存 | `-Undo` 后电脑 A 只剩 APIPA、无网关/DNS | v2 整口切静态（保留原配置）再追加 |
| 2 | 只看 `netsh` 退出码 | 「已是 DHCP」时 netsh 返回非 0 被误判为失败 | 改用 `Get-NetIPInterface` 复核状态 |
| 3 | `($x \| ForEach-Object { $_.IPAddress } -join ', ')` | PS 5.1 把 `-join` 当参数 → 抛异常（语法检查查不出） | `$x.IPAddress -join ', '` |
| 4 | 自动选网卡未排除虚拟网卡 | B 机把 `192.168.1.20` 加到 **VMnet1** 上，A 完全收不到 | `Test-VirtualAdapter` 过滤 + 优先有默认网关的物理网卡 + 选中后告警 |
| 5 | `check-logs.ps1` 第 93 行括号不匹配；判定不区分时间窗口 | 脚本直接崩；且把配 lab IP **之前**的 5 条历史「无合格网卡」算进来误报 FAIL | 补括号；新增 `-Since` 参数 |

> 教训：这些缺陷**没有一个能被"读代码"发现**，全是真跑才暴露。
> 与 §1.5.2（IP_MULTICAST_IF 实测）是同一条纪律：Windows 行为必须实测。

### 已实际执行的单机验证

- [x] **应用启动 + 发现服务启动**：日志显示
  `没有找到任何合格的私有 IPv4 网卡，局域网发现不会发送也不接收。`（对本机环境是正确结论），
  应用无崩溃、M1 身份照常加载（设备码 `M5WC-14GX`、指纹前缀 `89A5C10E`）。
- [x] **真实 socket 接收 + 来源过滤（早期一次构建上验证）**：
  用脚本向本机 45872 发送 1 个合法 announcement 与 2 个畸形报文，
  日志出现 3 条 `丢弃发现报文：来源地址非 IPv4 / 非 RFC1918 / 不在任何本机子网内（累计=1/2/3）`——
  证明 socket 确实收到数据、来源过滤在 JSON 之前生效、畸形报文不会让程序崩溃。
  > 该证据来自「无合格网卡时仍创建 receiver」的那一版构建；
  > 之后为避免无谓占用端口改成「无合格网卡则不开 socket」，因此最终构建上不再有接收日志。
- [ ] 真实 accept 路径（收到合法报文 → 进缓存 → 显示在 UI）：**未执行**
  ——本机没有任何 RFC1918 接口，无法构造可信来源。该路径由 189 条 Protocol 单元测试覆盖
  （codec + evaluator + cache + runtime state），但**没有**走真实 socket。
- [ ] UI 按钮点击：仍未做（无 UI 自动化框架），**不伪装成已验证**。

首次运行可能触发 Windows 网络访问提示：防火墙规则属于 M10，本轮**没有**在程序里自动改防火墙。

### 交给用户执行的两机验收清单（进入 M3 前的必需关卡）

条件：两台都跑最终 M2.1 build，且位于同一 RFC1918 IPv4 子网（例如 `192.168.1.10/24` 与 `192.168.1.20/24`）。

1. A 启动；2. B 启动；3. 3~6 s 内 A 看到 B；4. 3~6 s 内 B 看到 A；5. 自己不会看到自己；
6. 等 20 s 无重复设备；7. B 退出；8. A 在约 7~9 s 后移除 B；9. B 重启；10. B 再次出现；
11. B 关闭 `AllowDiscovery`；12. A 在 TTL 后移除 B；13. B **仍然**可以看到 A；
14. B 点刷新**仍然**能 Probe 到 A；15. B 再打开 `AllowDiscovery`；16. A 再次发现 B；
17. A 点刷新；18. B 应能正常响应；19. 两边应用均无崩溃；20. 日志无 Access Key。

补充（若环境允许）：一台 Ethernet + 一台 Wi-Fi 但同一 subnet 的组合要优先测；
若有一台机器同时具备 Wi-Fi + Ethernet，额外确认组播没有全部错误地走系统默认接口
（这正是 §1.5.2 修复的目标）。

> 验收结果请回填到第 9 节与第 1 节「是否满足完整 M2 DoD」。
> **未回填前不要进入 M3。**

### 首次实测失败记录（2026-09-20，NOT RUN 原因已实证）

**用户于 2026-09-20 15:52 完成首次两机实测：两台设备列表均为空，失败。**

现场数据：

| | 电脑 A（DESKTOP-D132BMD） | 电脑 B（DESKTOP-CU2Z63D） |
|---|---|---|
| IPv4 | `172.100.166.220` | `172.100.166.65` |
| 默认网关 | `172.100.166.254` | — |
| 链接速度 | 1000 Mbps | 1000 Mbps |
| 网卡 | Realtek PCIe GbE | Realtek Gaming 2.5GbE |
| 设备码 | `M5WC-14GX` | `3ERD-R74V` |

**根因（不是 LanRemote 的 bug）**：RFC1918 的 172 段只覆盖 `172.16`–`172.31`，
而这两台机器是 `172.100.166.x`（第二段 **100**），**落在范围外，属于公网地址段**。
`NetworkInterfaceSelector` 据此正确判定「没有合格的私有 IPv4 网卡」，
`LanDiscoveryService` 因此不开 socket → 列表为空。软件行为符合规格与 ADR-022。

两台机器网线是通的（1000 Mbps、同网段、可互通），问题纯粹在地址段本身。

**处置**：新增 `scripts/acceptance/set-lab-ip.ps1`，给两台机器各安排
`192.168.1.10/24`（A）与 `192.168.1.20/24`（B），同时把网卡配置文件设为 `Private`
并放行入站 UDP 45872；`-Undo` 可一键回滚。**未修改任何产品代码**，
安全约束（只认 RFC1918）保持原样。验收手册新增第 1.1 / 1.2 节记录本案例。

> 该脚本第一版（v1）按「保留 DHCP 地址 + 追加静态地址」实现，被实测证明前提错误，
> 已归档为 `_set-lab-ip.v1.broken.ps1.bak`。当前是 **v2**，细节见下一小节。

### 验收核心已实证：A 机日志中的双向发现记录（2026-09-20 16:10–16:16）

回滚事故中断验收之前，电脑 A 的 `%LOCALAPPDATA%\LanRemote\logs\lanremote-20260920.log`
**已经完整记录了 M2.1 两个头条修复的真实收发**。摘录（原文）：

```
16:10:15.717 [Information] 局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10
16:10:25.396 [Debug]      已回应来自 192.168.1.20 的 probe：unicast → 192.168.1.20:45872（不使用源端口 50193）。
16:10:25.401 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。
16:16:18.730 [Information] 设备离线：DESKTOP-CU2263D（3ERD-R74V）192.168.1.20。
16:16:19.378 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。
```

据此可判定：

| M2.1 修复项 | 证据 | 结论 |
| --- | --- | --- |
| §1.5.1 probe 回应端口 | `不使用源端口 50193` → `192.168.1.20:45872` | **真实链路上成立**（源端口 50193 是随机的，若仍回源端口则 B 收不到） |
| §1.5.2 组播出口网卡 | 单网卡 + 绑定源地址后 announce/probe 正常互达 | 未出现 `AddressNotAvailable`，出口选择正确 |
| 发现 → 缓存 → UI | `发现设备` 与 `设备离线` 成对出现，且离线后重新出现 | TTL 移除 + 重新发现路径**真实可用** |

> 16:14 之后反复出现的 `发送组播 announce 到 239.255.77.77 失败（SocketError=AddressNotAvailable）`
> 是**回滚把网卡绑定的源地址弄丢之后**的后果（当时以太网只剩 APIPA），不是产品缺陷。
> 该进程（pid 37700，源地址已消失仍在跑）已手工终止。

**所以：M2.1 的技术结论已被真实环境支撑，缺口只剩「20 步完整走完并正式签字」。**
这一项仍是 **NOT RUN**——证据是**中途截取**的，不是按清单逐条验收的结果。

### set-lab-ip.ps1 v1 事故与 v2 重写（2026-09-20）

**事故**：用户在电脑 A 上跑 `set-lab-ip.ps1 -Role A`（报告成功）、`check-env.ps1`（PRE-CHECK PASSED）、
随后 `-Undo` —— 以太网**失去全部可用 IPv4**（仅剩 APIPA `169.254.194.126`，无网关无 DNS）；
第二次 `-Undo` 又抛出 `No IPv4 adapter found. Pass -InterfaceAlias explicitly.`。
已用 `Remove-NetIPAddress` + `Set-NetIPInterface -Dhcp Enabled` + `ipconfig /renew` 手工恢复，
复核 `172.100.166.220/24`、`Dhcp=Enabled`、网关 `172.100.166.254`、DNS `172.100.162.101/102` 均复原。

**根因（实测，不是推测）**：Windows IPv4 上「DHCP 地址 + 额外静态地址」**不能共存**。

| 操作 | 预期 | 实测 |
| --- | --- | --- |
| `New-NetIPAddress` 在 DHCP 接口上追加 | 共存 | 接口 `Dhcp` → `Disabled`，租约丢失 |
| `netsh interface ipv4 add address` 追加 | 共存 | 同上 |
| 追加后再删除 | 复原 | 只剩 APIPA，无网关/DNS |

（在定位过程中本机网络又断了一次，同样用 `netsh ... source=dhcp` + `set dnsservers source=dhcp`
+ `ipconfig /renew` 恢复。**教训与 §1.5.2 一致：Windows 网络行为必须实测，不能凭直觉断言。**）

**v2 改法**：整口切静态 —— 先把当前 IP/掩码/网关/DNS 存盘到
`%TEMP%\lanremote-lab-ip-state.json`，再用**同一套配置**切成静态（不断网），
最后追加 `192.168.1.x/24`（不带网关）。`-Undo` 移除 lab 地址、切回 DHCP、`ipconfig /renew` 并自检。

**v2 实测（本机，Windows PowerShell 5.1）**：

| 场景 | 结果 |
| --- | --- |
| `-Role A` | `EXIT=0`；以太网 `172.100.166.220` Manual + `192.168.1.10` Manual 共存 |
| 切静态后连通性 | `ping 172.100.166.254` = True；DNS `172.100.162.101/102` 保留；默认路由仍在 |
| `-Undo` | `EXIT=0`；回到 `172.100.166.220/24 Dhcp`；`OK:` 自检通过 |
| 已是 DHCP 时再 `-Undo`（幂等） | `EXIT=0`（v1 在此场景崩溃） |

**v2 修掉的两个 bug**（都是实测才暴露）：

1. **不能只看 `netsh` 退出码** —— 在已是 DHCP 的接口上 `set address source=dhcp`，
   netsh 返回**非 0** 并输出「已在此接口上启用 DHCP。」，实为成功。改为**用
   `Get-NetIPInterface` 复核实际状态**，退出码仅供参考。
2. **`($x | ForEach-Object { $_.IPAddress } -join ', ')` 在 PS 5.1 会把 `-join` 当成
   `ForEach-Object` 的参数**并抛异常（语法检查查不出来）。已改为 `$x.IPAddress -join ', '`。

附带修正：`Set-NetConnectionProfile` 在刚切完静态时会因网卡处于 `Identifying...` 而失败，
现改为最多 5 次、间隔 3 s 重试。（即便失败也不阻断——防火墙规则是 `Profile Any`，
不依赖网络配置文件。）

### 验收物料（已就绪，等用户重测）

| 项 | 位置 |
|---|---|
| 两机验收手册（20 步 + 排查表 + 回填模板） | `docs/TWO_MACHINE_ACCEPTANCE.md` |
| 便携版验收包（自包含 win-x64，解压即用，目标机无需装运行时） | `scripts/acceptance/make-package.py` 生成 `LanRemote-0.1.0-m2-win-x64.zip` |
| 验收前环境自检脚本 | `scripts/acceptance/check-env.ps1` |
| 验收后日志检查 / 访问密钥泄漏扫描脚本 | `scripts/acceptance/check-logs.ps1` |
| 私有实验网段配置 / 回滚脚本（**v2**） | `scripts/acceptance/set-lab-ip.ps1`（v1 缺陷版已归档为 `_set-lab-ip.v1.broken.ps1.bak`） |

包内自带 `START-HERE.md`（即验收手册）、`check-env.ps1`、`check-logs.ps1`、`set-lab-ip.ps1`。
两个 `.ps1` 刻意存为 **UTF-8 with BOM**，否则 Windows PowerShell 5.1 会把中文按 ANSI 解析成乱码。

## 10. 修改文件

**新增（Discovery 项目）**

| 路径 | 说明 |
|---|---|
| `Networking/Ipv4Math.cs` | 地址/掩码数学、directed broadcast、连续前缀掩码校验 |
| `Networking/PrivateIpv4.cs` | RFC1918 / loopback / link-local 判定 |
| `Networking/NetworkBinding.cs` | `NetworkBinding` / `Ipv4UnicastAddress` / `NetworkInterfaceSnapshot` |
| `Networking/INetworkInterfaceSource.cs`、`SystemNetworkInterfaceSource.cs` | 网卡枚举抽象与真实实现 |
| `Networking/VirtualAdapterFilter.cs` | 虚拟/VPN 适配器关键词过滤 |
| `Networking/NetworkInterfaceSelector.cs` | 最终筛选规则 |
| `Networking/INetworkBindingProvider.cs`、`LocalNetworkBindingProvider.cs` | 绑定快照（含 `Refresh()` 供 M9 热插拔） |
| `Networking/SubnetPolicy.cs` | `ISubnetPolicy` 实现 |
| `Networking/SourceEndpointFilter.cs` | JSON 之前的来源地址准入 |
| `Protocol/DiscoveryPackets.cs` | announcement / probe DTO（无地址字段） |
| `Protocol/DiscoveryPacketCodec.cs` | camelCase JSON、长度上限、JsonException 吞掉 |
| `Protocol/DiscoveryAnnouncementEvaluator.cs` | 全项严格校验 + 地址取自 source |
| `DiscoveryRuntimeState.cs` | 不可变快照 + 线程安全状态 |
| `DiscoveryDeviceCache.cs` | 有界缓存 + TTL |
| `LanDiscoveryService.cs` | UDP 收发与三个后台循环 |

**修改**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Discovery/DiscoveryConstants.cs` | 新增 type 常量、`ExpectedTransportPort`、nonce/名称/capability 限制、缓存与队列容量 |
| `src/LanRemote.Discovery/LanRemote.Discovery.csproj` | 新增 `Microsoft.Extensions.Logging.Abstractions` |
| `src/LanRemote.Core/Abstractions/IDiscoveryService.cs` | **唯一一处 M2 API 扩展**：新增 `ProbeAsync`，Start/Stop/Watch 签名未改 |
| `src/LanRemote.Core/Infrastructure/AppVersion.cs`（新增） | 从程序集读版本，供 announcement 的 `appVersion` |
| `src/LanRemote.App/App.xaml.cs` | DI 注册；`OnExit` 先 `StopAsync` discovery 再停宿主 |
| `src/LanRemote.App/ViewModels/MainViewModel.cs` | 发现集成、UI 线程更新、TTL 清理、Refresh |
| `src/LanRemote.App/MainWindow.xaml` / `.xaml.cs` | 真实设备列表、刷新按钮、disabled 的连接按钮 |
| `Directory.Build.props` | 版本 `0.1.0-m1` → `0.1.0-m2` |

### M2.1 本轮改动

**新增**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Discovery/Protocol/DiscoveryReplyTarget.cs` | internal 纯函数：probe 回应目标 = 源地址 + 固定端口 45872 |
| `src/LanRemote.Discovery/Networking/MulticastInterfaceOption.cs` | internal 纯函数：`IP_MULTICAST_IF` 选项值（接口 IPv4 的网络序 4 字节） |
| `src/LanRemote.Discovery/AssemblyInfo.cs` | `InternalsVisibleTo("LanRemote.Protocol.Tests")`，仅为测试开放上述两个 internal 类型 |
| `tests/LanRemote.Protocol.Tests/DiscoveryReplyTargetTests.cs` | 回应端口回归测试（含 `ProbeReply_AlwaysTargetsDiscoveryPort_NotSourcePort`） |
| `tests/LanRemote.Protocol.Tests/MulticastInterfaceOptionTests.cs` | 选项值构造 + 真实 socket 上设置不抛异常 |
| `docs/TWO_MACHINE_ACCEPTANCE.md` | 两机验收手册（20 步 + 排查表 + 结果回填模板） |
| `scripts/acceptance/check-env.ps1` | 验收前环境自检（RFC1918 判定 / 端口占用 / 网络配置文件 / 残留进程） |
| `scripts/acceptance/check-logs.ps1` | 验收后日志检查 + 访问密钥泄漏扫描（命中只打掩码，不回显真实密钥） |
| `scripts/acceptance/set-lab-ip.ps1` | 追加/移除私有 lab IPv4（`-Role A\|B` / `-Undo`）；本机网络非私有段时必须先跑 |
| `scripts/acceptance/make-package.py` | 把 publish 产物打成便携版验收 zip |
| `scripts/acceptance/add-bom.py` | 给两个 `.ps1` 加 UTF-8 BOM（PS 5.1 否则乱码） |

**修改**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Discovery/LanDiscoveryService.cs` | ① 回应目标改用 `DiscoveryReplyTarget.ForProbe(remote)`；② sender 增加 `MulticastInterface`；③ socket 创建失败时 `cts.Dispose()`；④ probe 回应成功时补一行 Debug 日志（§1.5.6） |
| `src/LanRemote.Discovery/Protocol/DiscoveryAnnouncementEvaluator.cs` | capabilities：raw 数量先判上限、空白/null 拒绝、控制字符在 Trim 前拒绝 |
| `src/LanRemote.App/ViewModels/MainViewModel.cs` | `StartDiscoveryAsync` 改返回 `bool`；`LoadAsync` 只在成功时写正常状态 |
| `tests/LanRemote.Protocol.Tests/DiscoveryAnnouncementEvaluatorTests.cs` | 删掉语义错误的 `Capabilities_AreDedupedAndEmptiesRemoved`，换成 6 组新测试 |

**未改动**：M1 全部安全实现（`DpapiSecretVault`、`DpapiAccessSecretStore`、`SecretGenerator`、
`DeviceCertificateService`、`DeviceIdentityService`、`CrockfordBase32`、`DeviceCode`）。
尤其没有重新引入 `PersistKeySet` / `Exportable` / `KeyEncipherment`。

## 11. 构建

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

**M2.1 真实执行结果：PASS** —— 12 个项目全部生成，**0 个警告，0 个错误**（耗时 00:00:12.15）

**M3 第 24 步「先修再跑」后重新执行：PASS —— 0 个警告，0 个错误**（耗时 00:00:12.71）。
本轮产品代码只加了 `TlsConnection.LocalEndPoint`（只读），验收器改动见 §15 步骤 24。

## 12. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

**M2.1 真实执行结果：408 passed / 0 failed / 0 skipped**
**M3 阶段 5 后：573 passed / 0 failed / 0 skipped**
**M3 第 24 步「先修再跑」后：574 passed / 0 failed / 0 skipped**（本轮 +1）

| 项目 | M1.3 后 | M2 后 | M2.1 后 | M3 阶段 5 后 | 本轮后 |
|---|---:|---:|---:|---:|---:|
| LanRemote.Core.Tests | 125 | 125 | 125 | 125 | 125 |
| LanRemote.Security.Tests | 65 | 65 | 65 | 66 | 66 |
| LanRemote.IntegrationTests | 3 | 3 | 3 | 3 | 3 |
| LanRemote.Protocol.Tests | 7 | 189 | 215 | 215 | 215 |
| LanRemote.Transport.Tests | 0 | 0 | 0 | 164 | **165** |
| **合计** | **200** | **382** | **408** | **573** | **574** |

既有 408 条全部继续通过（**没有删除任何测试换取通过**）。
唯一被替换的测试是 `Capabilities_AreDedupedAndEmptiesRemoved`——
它的语义（空 capability 被静默移除后仍接受整条报文）与 M2.1 收紧后的规格直接冲突，
按规格要求重写为 6 组更严格的新测试。

**本轮新增的那 1 条**：

| 测试 | 断言 |
|---|---|
| `Connect_Reports_Local_End_Point_Matching_Server_Observation` | 客户端自报的 `LocalEndPoint` 必须与服务端 accept 时看到的 `RemoteEndPoint` **同一**（地址 + 端口），且释放后返回 `null` 而不是抛异常 |

> 为什么「只是个观测量」也要对侧断言：只断言非空的话，把它实现成「返回任意本地端口」
> 测试照样绿。而错的配对键会让两份日志**配错行**，比没有配对键更坏。
> 为此给测试用 `TestTlsServer` 加了 `AcceptedRemoteEndPoints`（accept 循环里记录远端端点）。

**M2.1 那一轮新增的 26 条测试**（保留，供追溯）：

| 测试 | 断言 |
|---|---|
| `ProbeReply_AlwaysTargetsDiscoveryPort_NotSourcePort` | 源 `192.168.1.20:53742` → 目标 `192.168.1.20:45872`，且 `NotEqual(53742)` |
| `ProbeReply_IgnoresAnySourcePort`（5 组） | 任意源端口（含 45871/45873 这两个最容易被写错的邻近端口）都不影响目标端口 |
| `ProbeReply_KeepsSourceAddressOnly` | 只有地址来自报文，端口恒为常量 |
| `ProbeReply_RejectsNull` / `RejectsNonIpv4` | 入参防御 |
| `Capabilities_RawCountAboveLimit_IsRejectedEvenWhenDuplicates` | 17 个全重复的 `view` → 拒绝（去重后是 1 项也不能放过） |
| `Capabilities_WhitespaceEntry_IsRejected`（5 组） | `""` `" "` `"   "` `"\t"` `"\r"` → 拒绝 |
| `Capabilities_NullEntry_IsRejected` | `null` → 拒绝 |
| `Capabilities_ControlCharacter_IsRejected`（4 组） | `"evil\nfake-log"` `"abc\rxyz"` `"abc\txyz"` `"view\n"` → 拒绝 |
| `Capabilities_ValidDuplicates_AreDeduped` | 合法重复项去重；`" control "` Trim 后生效 |
| `Capabilities_ExactlyMaxEntries_IsAccepted` | 恰好 16 项 → 接受 |
| `MulticastInterfaceOptionTests`（5 条） | 选项值 = 网络序 4 字节；不同 binding 产出不同值；拒绝 null/非 IPv4；**真实 socket 上设置不抛异常并回读一致** |

新增测试文件（都在 `LanRemote.Protocol.Tests`，未新建第五个测试项目）：

| 文件 | 覆盖 |
|---|---|
| `PrivateIpv4Tests.cs` | §46 RFC1918 边界（10/172.16/192.168 上下界）、127/169.254/公网、§47 directed broadcast 数学、掩码连续性 |
| `SubnetPolicyTests.cs` | §8 全部 8 组数据驱动用例；公网/169.254/IPv6/未知 local/null；「都私有但不同网段」；多网卡只匹配对应 binding |
| `NetworkInterfaceSelectorTests.cs` | §48 全部 10 类场景（Up/Down/无线/公网/169.254/禁类型/虚拟VPN/无掩码/一卡多地址/混合）＋真实 OS 枚举不抛异常 |
| `DiscoveryPacketCodecTests.cs` | roundtrip、camelCase、§49 的畸形输入（空/随机字节/非法 UTF-8/截断 JSON/`{}`/非对象/超大）、nonce、§9 端口护栏 |
| `DiscoveryAnnouncementEvaluatorTests.cs` | §13 全项校验、§50 来源地址权威、§51 自公告丢弃、未知 capability 放行、能力去重与上限 |
| `DiscoveryDeviceCacheTests.cs` | §52 add/update/换 IP 仍一条/过期/未过期/容量有界/满时先清过期 |
| `DiscoveryRuntimeStateTests.cs` | §53 AllowDiscovery 决策、Initialize 前拿不到数据、capabilities 不提前宣布 multi-monitor |
| `LanDiscoveryServiceLifecycleTests.cs` | §33 Start/Stop 幂等、无网卡不抛、Stop 后 WatchAsync 自然结束 |

## 13. ADR

`docs/DECISIONS.md` 新增：

- **ADR-022 — Discovery transport**：IPv4 only；UDP 组播 + directed broadcast probe；
  无云端/NAT 穿透；source IP 权威；报文一律不可信；announce 不是认证依据。
- **ADR-023 — WatchAsync upsert-only + UI TTL prune**：
  明确「不发送 Removed 假设备，UI 按 LastSeen 自 prune」，避免下一位 AI 在 M2 临时推翻现有 Core API。

### 2026-09-20 追加：产品形态决策（用户拍板「三个都做，含改 IP 一键」）

`docs/DECISIONS.md` 新增三条。**只定规则与验收口径，当前不启动编码**：

- **ADR-024 — 网络诊断必须进 UI**：`bindings.Count == 0` 不能再只写日志；UI 必须给出
  「原因 + 网卡名 + 实际地址」，至少覆盖 `05_UI_UX_SPEC.md` §8 的「未发现设备 / 防火墙阻止 / 网络断开」。
  实施时机 **M9**，最晚 M10 发布前闭合。
- **ADR-025 — 防火墙放行内置 App + UAC**：一键按钮 + UAC 提权；
  `configure-firewall.ps1` / `remove-firewall.ps1` **降级为可选离线入口**；规则仍只放行 LocalSubnet，
  且必须自带「按前缀精确撤销」契约。实施时机 **M10**。
- **ADR-026 — 临时私有地址一键：显式 / 确认 / 可撤销，禁止静默自动改 IP**：
  用户显式选网卡 + 排除虚拟网卡 + 先整张切静态再追加 + 撤销回 DHCP 并自校验。
  实施时机 **不早于 M10**。

**同时修掉一处编号缺陷**：ADR-016 声明「SUPERSEDED by ADR-018」，但那条记录被错标成了
「ADR-021（Key Usage）」，导致 ADR-018 实际不存在。已改回 **ADR-018**，并在条目顶部加了修正说明；
真正的 Key Usage 决策仍是 ADR-021，两者不要合并。

**M2.1 本轮未新增 ADR**：修的都是既有决策下的实现缺陷，没有推翻或新增架构决策。

### 2026-09-21 追加：M3 红队评审产出两条 ADR

外部模型对 M3 设计做了红队评审，分流结果见 **`docs/M3_REVIEW_TRIAGE.md`**。据此新增：

- **ADR-027 — 发现层身份冲突不得静默 last-write-wins**：`DiscoveryDeviceCache` 是
  `Dictionary<Guid,...>` + `Upsert` 覆盖式更新（读代码确认），同 deviceId 换指纹会静默覆盖。
  **M3 不动 M2 已验收行为**（只用连接目标不可变快照止血）；冲突语义最晚 **M4** 落地：
  同 deviceId + **不同指纹** → `IdentityConflict` 并禁用连接；同 deviceId + **同指纹** + 不同 IP
  是多网卡良性广播，**不得误杀**。
- **ADR-028 — M3→M4 身份绑定契约**：连接上下文必须不可变携带
  `{deviceId, endpoint, expectedPin, presentedPin}`；**M4 的 transcript 必须绑定 `presentedPin`**
  （M3 实际出示证书的指纹），只绑 `expectedPin` 不够——否则存在凭据中继缺口。
  `presentedPin` 是 **M3 的交付物**。

### 2026-09-21 追加：ADR-018 风险已实测收口 → **ADR-029**

- **ADR-029 — 证书加载改用 `X509KeyStorageFlags.Default`（0）**，同时作废 ADR-016 与 ADR-018。
- 实测矩阵（真实 `SslStream` server/client，loopback，3 flag × 3 协议 × 重复 3 次）：
  - `EphemeralKeySet`：**9/9 FAIL**。服务端 `AuthenticationException: ... platform does not support
    ephemeral keys.` ← `Win32Exception: 安全包中没有可用的凭证（0x8009030E）`；
    客户端只看到 `IOException: unexpected EOF`。
  - `PersistKeySet`：9/9 OK，但**磁盘留下持久密钥副本**（`%APPDATA%\Microsoft\Crypto\Keys` 文件数
    dispose+GC 后仍 +1）。
  - `DefaultKeySet`(0)：9/9 OK，且密钥文件在 dispose/GC 后**删除**。
    （`X509KeyStorageFlags` 没有 `Default` 成员，正确名字是 `DefaultKeySet`，值同为 0。）
- **⚠️ 污染陷阱**：同一进程先 `PersistKeySet` 导入过同一私钥后，再 `EphemeralKeySet` 导入 → **握手成功**。
  所以「测试里没报错」不等于 flag 可用；结论必须在新进程、顺序受控下测。
- **M3 第一步**：改 `ImportFlags` → `Default`，**并改掉锁住旧选择的测试**
  `DeviceCertificateTests.ImportFlags_UsesEphemeralKeySetOnly`，然后跑通真实握手集成测试。
两条不变量已就近写进代码注释与类型 XML doc（`DiscoveryReplyTarget`、`MulticastInterfaceOption`），
并由单元测试锁住。

### 2026-09-21 追加：ADR-029 被再次收紧 → **ADR-030**

- **ADR-030 — `CertificateRequest.CreateSelfSigned()` 直接产出的证书，私钥是 ephemeral 的，
  不能用作 Windows TLS 服务端凭据；必须经「导出 PFX → `DefaultKeySet` 导入」往返。**
- 实测：服务端在 `AcquireCredentialsHandle` 处抛
  `AuthenticationException: Authentication failed because the platform does not support ephemeral keys.`
  ← `Win32Exception 0x8009030E`；客户端只看到 `IOException: unexpected EOF`。
- **关键推论**：这与 ADR-029 的 `EphemeralKeySet` 失败是**同一个错**。说明 Schannel 拒绝的是
  **密钥的 ephemeral 属性本身**，而不是某个导入 flag 的名字。
  ADR-029 改 flag 只是必要条件；`DeviceCertificateService` 里那步 PFX 往返**不是冗余代码**。
- 守护测试：`SelfSignedKeyEphemeralTests.CreateSelfSigned_Certificate_Cannot_Serve_Tls_Without_Pfx_Roundtrip`。

## 14. 已知问题 / 技术债

1. ~~**M2 手工 DoD 未完成**~~ —— **已于 2026-09-20 两机验收 PASS（20/20）关闭**，见第 9.1 节。
   只有改动 discovery 收发逻辑（网卡筛选 / probe / announce / 缓存 TTL）才需要重跑。
2. **本机开发环境无法验证发现**（未变）：唯一活跃网卡是 `172.100.166.220`（公网段）。
   若将来要**单机**验证，需要改用 192.168 / 10.x 的网络，或另开实验性开关（当前不做）。
   两机验证请直接按 §9.1 用 `set-lab-ip.ps1` 搭临时私有地址。
2a. ~~**组播出口网卡只验证到 socket option 回读**~~ —— **两机验收已证伪风险关闭**：
   B 机带 5 张私有网卡，A 机只在 `192.168.1.20` 收到 B，无跨网卡串扰、无 `AddressNotAvailable`。
3. **网卡热插拔未处理**：`StartAsync` 做一次快照并保持运行期不变；`INetworkBindingProvider.Refresh()`
   已预留，M9 再接 `NetworkChange`。
4. **虚拟网卡过滤是启发式**：可能误杀名字里带 `tap`/`vpn` 的真实网卡；需要 VPN LAN 时应另开实验设置。
5. **真实 accept 路径未走真实 socket 验证**（M3 范围，第 15 节）。
6. UI 交互无自动化覆盖（累计遗留）。
7. 日志无轮转（M9，ADR-013）。
8. `LanRemote.Sessions` / `Capture` / `Input` 仍是空项目占位。
9. **UI 网络诊断缺失（规格违反，待修）**：`05_UI_UX_SPEC.md` §8 要求区分「未发现设备 / 防火墙阻止 /
   网络断开」，当前 discovery 启动失败只写日志，UI 仍显示「已从磁盘加载配置与本机身份」。
   已立 **ADR-024**，实施时机 **M9**，最晚 M10 发布前闭合。
10. **防火墙交付形态待改**：M10 原任务只有 `configure-firewall.ps1` / `remove-firewall.ps1`，
    等于要求终端用户开管理员 PowerShell。已立 **ADR-025**：改为 App 内一键 + UAC，ps1 降级为可选离线入口，
    且必须有「按前缀精确撤销」契约。实施时机 **M10**。
11. **本机开发残留**（非阻断）：`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`
    是 M1.3 手工重置身份时留的旧开发备份，确认不再需要后由施工环境手工删除。
    **硬约束：绝不把「自动删除身份备份」写进产品逻辑。**
12. **`TransportHost` 完全没有 logger（可观测性缺口，2026-09-21 建 M3 验收器时发现）**：
    构造函数不收 `ILogger`，三条拒绝路径全是静默 `return`——
    ① 同子网校验失败（`HandleAsync` 第 260 行）、② 准入限额拒绝（第 266 行）、
    ③ TLS 握手失败（第 300 行块）。后果：被控端对「谁被拒了、为什么拒」一无所知，
    两机验收时**只能靠控制端输出**判定。
    与之相对，`ControlPreAuthSession` 的超时（发生在会话处理器**内部**）是有输出的
    （`rejection=pre-auth-timeout`）——所以缺口精确落在 `TransportHost` 这一层，不含 pre-auth 会话层。
    与第 9 条（UI 诊断）同源，建议并入 **ADR-024 / M9** 一起做，M3 不为它改产品代码。
13. **跨子网拒绝没有真机覆盖**：现有 lab 是两机同挂 `172.100.166.x` + `192.168.1.x`，
    host 只听 RFC1918 绑定，而 Windows 会自动挑同子网源地址，做不出「源 IP 在另一子网」的样本；
    `New-NetRoute` / `route add` 都不能指定源地址，除非给 `TlsClientConnector` 加本地绑定参数。
    当前覆盖全在自动化测试（见第 15 节步骤 24 的表）。**要真机补这一条，需要用户提供第三子网或批准改产品代码。**
14. **五个阶段 deadline 的具体取值尚未经第二轮评审**（2026-09-21 记）：
    `connect 3s / handshake 5s / lengthPrefix 5s / payload 10s / hello 5s`。
    已验证的只是「**执行得准**」（绝对时限在 0–36 ms 误差内生效，`slow-dribble` 也证了不可重置），
    **不是「取值合理」**。慢网络 / 高延迟下 3 s 的连接与 5 s 的握手是否会误杀，
    需要第二轮外部评审判定。这是「数值 vs 机制」的分界，别把机制已验当成数值已验。
15. **验收器与产品共用一个进程，不是隔离的**：`ClientRole` / `HostRole` 在同一进程内
    调产品代码。好处是接线与 `App.xaml.cs` 一致、能测到真实的 DI 路径；
    代价是产品里的静态状态（若有）会跨场景泄漏——**已发现并修过一个**：
    每个场景新建 `AcceptanceContext` 却从不停止 discovery，4 个场景残留 4 个 UDP socket，
    失败现象伪装成「发现不到对端」。现由 `IAsyncDisposable` + `StopAsync` 收口。
    **新增场景时务必确认 context 被释放**。
16. **`--address/--pin` 直连模式下 `pin-mismatch` 的自检有循环性**（2026-09-21 记）：
    该模式不知道对端真指纹，只能「翻转你给的 pin 一位」。所以若你给的本来就是错的 pin，
    翻一位后可能翻回真指纹 → 场景反而 PASS/FAIL 反了（变异矩阵真的撞上过这个双翻）。
    两机验收不受影响（pin 来自 discovery，是真值）。**自检时按提示传真指纹。**

## 15. 下一步 —— M3（TLS Host/Client + 同子网连接校验）

**已实施完毕（2026-09-21，含两机验收 PASS）**：下面是本阶段的施工顺序与逐步记录，原记的「等开工」已不适用。
**开工前必读 `docs/M3_REVIEW_TRIAGE.md`**（外部红队评审 + 本机实测校正）。

**可直接复用，不要重写**（M2 已就绪）：`ISubnetPolicy` / `SubnetPolicy` / `NetworkBinding` /
`LocalNetworkBindingProvider` / `NetworkInterfaceSelector` / `TransportConstants`。

### 阶段 0 —— 先修正证书载入（ADR-029，阻塞后面所有步骤）

1. ✅ **已完成** 把 `DeviceCertificateService.ImportFlags` 改成 `X509KeyStorageFlags.DefaultKeySet`（值 0）。
2. ✅ **已完成** 改掉把旧选择锁成断言的测试：
   `ImportFlags_UsesEphemeralKeySetOnly` → **`ImportFlags_UsesDefaultWithoutPersistOrExport`**，
   断言改为 `DefaultKeySet` 并显式禁止 `EphemeralKeySet` / `PersistKeySet` / `Exportable` / `MachineKeySet`。
3. ✅ **已完成** 新增 `tests/LanRemote.Security.Tests/DeviceCertificateTlsHandshakeTests.cs`
   （真实 SslStream 握手，ADR-018 遗留的强制要求）：断言回调被执行 ∧ pin 字节匹配 ∧
   服务端无异常 ∧ 协商协议 ∈ {Tls12, Tls13} ∧ 真实帧收发往返（`pong:hello-lanremote`）。
   **已做变异验证**：把 flag 改回 `EphemeralKeySet` 后，本测试与步骤 2 的测试**同时失败**
   （本测试报 `IOException: Received an unexpected EOF...`），证明守卫不是空断言；随后已还原。
   ⚠️ 注意污染陷阱：同一进程先 `PersistKeySet` 导入过同一私钥后 `EphemeralKeySet` 会碰巧成功，
   所以结论必须在新进程 / 顺序受控下测，不要依赖"没报错"。
4. ✅ **已完成** `dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **409 PASS / 0 FAIL**
   （原 408 + 新增 1）。**阶段 0 通过，已进入阶段 1。**

### 阶段 1 —— 连接目标与身份契约（TOCTOU / ADR-028）

5. ✅ **已完成** 定义**不可变连接目标快照** `{deviceId, remoteIPv4, tcpPort, expectedCertSha256(32B)}`，
   在用户点击连接的那一刻冻结；握手期间**不得**回读 discovery 缓存。
   - 产物 `src/LanRemote.Transport/ConnectionTarget.cs`（`TryCreate(DiscoveredDevice)` /
     `TryCreate(Guid, IPAddress, int, string)`；指纹**复制**进内部数组，只暴露 `ReadOnlyMemory`）。
   - 验收用例 `TlsClientConnectorTests.Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`：
     真实握手 + `DiscoveryDeviceCache`，服务端闸门保证「改缓存」发生在 TCP 已连上、TLS 未完成之间；
     改完仍按 A 校验成功，并从被污染的缓存重新冻结 → 按 B **失败**（对照组）。
     **已做变异验证**：把 pin 比较短路掉后，本用例的对照组与 `Connect_Rejects_Pin_Mismatch`
     同时失败（`No exception was thrown`），证明不是空断言。
   - ⚠️ 诚实边界：`ConnectAsync` 的参数类型就是快照、不持有缓存引用，所以「回读」在类型层面
     已不可表达；这个用例锁的是**这个契约**，不是在运行期抓到了一次真的回读。
6. ✅ **已完成** 定义**不可变连接上下文** `{deviceId, endpoint, expectedPin, presentedPin}`（ADR-028）。
   产物 `src/LanRemote.Transport/ConnectionIdentity.cs`：`presentedPin` 在证书校验回调里捕获、
   之后不可变、暴露给 M4；不保存 `IPEndPoint` 实例（其 `Port` 可写），每次调用返回新对象。
   另见步骤 13 的客户端校验器，它同时承担「pin 之外还有哪些不变量」。
7. ✅ **已完成** pin 解码与比较：连之前把 hex 解成**恰好 32 字节**，畸形 / 31 / 33 字节在
   `ConnectAsync` 之前失败；比较用 `cert.GetRawCertData()` +
   `CryptographicOperations.FixedTimeEquals`，**不比 hex 字符串**。
   产物 `src/LanRemote.Transport/CertificatePin.cs`
   （`TryDecode` 失败时 `out` 恒为 `Array.Empty<byte>()`；新增 `ReadOnlySpan` 比较重载避免临时分配）。

**阶段 1 附带产出（原本属于步骤 12/13，提前只为让步骤 5 的验收可测）**：

- `src/LanRemote.Transport/TransportTimeouts.cs`：五段**绝对**时限（连接 / 握手 / 长度前缀 /
  payload / hello），各自独立重新计时。**数值是初始值，不是实测结论**——步骤 15 实测后再调。
- `src/LanRemote.Transport/PeerCertificateValidator.cs`：客户端对服务端证书的校验。
  pin 是唯一安全边界；其后是形状不变量（ECDSA P-256 / 非 CA / KU 含 digitalSignature /
  EKU 含 serverAuth / 有效期）。**刻意不要求 `sslPolicyErrors == None`**——自签名必然触发
  `RemoteCertificateChainErrors`，本项目不用 CA、不用系统信任库（ADR-027）。
  时间用 `TimeProvider` 注入，**不改系统时钟**。
- `src/LanRemote.Transport/TlsClientConnector.cs` + `TlsConnection.cs`：客户端 TLS 连接器。
  `TargetHost = string.Empty`（**不发 SNI**——CN 是 `LanRemote-<设备码>`，放进 SNI 等于明文广播设备标识；
  pinning 下主机名校验不参与安全判定）。实测空串可用，握手正常完成。
- **阶段 1 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **464 PASS / 0 FAIL**
  （阶段 0 的 409 + 新增 55）。

#### M3 已实测记录（只写实际跑出来的，不预填）

| 场景 | 实测结果 |
| --- | --- |
| `CreateSelfSigned` 直出证书做服务端 | **失败**：`AuthenticationException: ... does not support ephemeral keys.` ← `Win32Exception 0x8009030E`；客户端只看到 `IOException: unexpected EOF`（ADR-030） |
| PFX 往返 + `DefaultKeySet` 后做服务端 | **成功**，协商到 TLS 1.3 |
| `TargetHost = string.Empty` | **成功**：不发 SNI、不做主机名校验，握手正常完成 |
| 对端 accept 后一直不握手，握手时限 400 ms | 客户端在 **~400 ms** 被切断，异常类型是 **`System.OperationCanceledException`**（实测 `GetType().FullName`，非推断） |
| 证书按真实时间有效、按注入时钟已过期 | 客户端抛 `AuthenticationException`，消息含 `expired`（说明是**我们的**校验器拦下的） |
| 同端口 + 两个不同具体地址 bind | **可以**（开不开 `ExclusiveAddressUse` 都行）→ 步骤 8 成立 |
| 同地址同端口第二次 bind | **被拒**，`AddressAlreadyInUse`(10048) |
| 别人先 bind `0.0.0.0`（未开 exclusive），我们再 bind 具体地址 | **仍成功** —— 开 exclusive 也**发现不了**这种情况 |
| 我们先 bind 具体地址（exclusive），别人再 bind `0.0.0.0` | **也成功** |
| 带 `SO_REUSEADDR` 的后来者抢同地址同端口 | 开不开 exclusive **都被拒**（`AccessDenied` 10013） |
| 连到具体地址的连接归谁 | 归**更具体**的 listener，不会被更宽的 socket 截走 |
| **步骤 15**：pre-auth 空闲，长度前缀时限 400 ms | **413 ms** 切断，`System.OperationCanceledException`，名额归零 |
| **步骤 15**：只发 2 字节（半截长度前缀） | **400 ms** 切断，`System.OperationCanceledException` |
| **步骤 15**：声称 64 字节只发 10 个（半截 payload） | **608 ms** 切断（时限 600 ms），`System.OperationCanceledException`；证明 payload 段有**自己的**预算，没被前缀段吃掉 |
| **步骤 15**：3 条 TCP 连上但永不握手，时限 400 ms | **436 ms** 内名额全部归还，**全程没有调用 `StopAsync`** |
| deadline 执行开销 | **0–36 ms** 量级，几百毫秒级时限可放心使用 |
| 取消时在 `SslStream` 上的异常类型 | **基类** `OperationCanceledException`（不是 `TaskCanceledException`） |
| 取消时在 `MemoryStream` 替身上的异常类型 | **`TaskCanceledException`**（替身内部 `Task.Delay(delay, token)` 抛的）——与上一行**不是同一个类型**，所以断言只要求「是取消」 |
| 变异：把 `CancelAfter` 挪进读循环（滑动窗口） | 低速攻击用例**变红**（8 字节全读完、1 s、无异常） |
| 变异：注释掉握手 `CancelAfter` | 握手卡死用例**变红**（名额 12 s 未归还） |
| `JsonSerializer.Deserialize<T>(utf8)` 会拦尾随内容吗 | 实测**不保证**——必须自己先用 `Utf8JsonReader` 探一次「恰好一个 JSON 值」 |
| 原始字符串字面量 `"""…\n…"""` | `\n` **不是转义**，是字面反斜杠（我自己先踩了一次，测试假红） |
| 变异：pre-auth 改用认证后的 1 MiB 额度（A-9） | 该用例 **720 ms** 以 `pre-auth-frame:…` vs `pre-auth-timeout` 变红 |
| 变异：`HelloFrame.TryParse` 无条件返回 true | **43 条**测试变红 |
| 变异：删掉客户端 `AllowTlsResume = false` | `Client_Options_Are_Explicit` 4 ms 内变红 |
| .NET 10 `SslClientAuthenticationOptions` 默认值 | `AllowTlsResume=True`、`AllowRenegotiation=True`、`EnabledSslProtocols=None` —— 三个都必须显式写 |
| .NET 10 `SslServerAuthenticationOptions` 默认值 | `AllowTlsResume=True`、`AllowRenegotiation=**False**`、`EnabledSslProtocols=None`（两端不对称） |

**⚠️ ADR-032：别过度声称 `ExclusiveAddressUse`**。我最初据此写的注释与测试断言是错的
（以为开了它就能发现"别人先占了更宽地址"）。实测矩阵如上：
**在本机可观测范围内，开与不开没有任何差别**。仍保留 `true`，但理由只能是
「防御 `SO_REUSEADDR` 语义更宽松的旧版 Windows」。

**⚠️ 教训：变异验证必须真的做红**。我第一版守护测试把 `ExclusiveAddressUse` 也开在了
**对照组**的 socket 上，于是「把被测属性改成 false」的变异**没有让测试失败**——
隔离错了，绿着也是空断言。是"变异后仍然绿"这件事暴露了它。

### 阶段 2 —— Listener / 准入 / 同子网校验

8. ✅ **已完成** 每张合格网卡起一个 TCP 45873 listener（`TransportHost.Start()`）。
   - **启动语义定为按网卡降级（ADR-031）**：某张网卡 bind 失败只记进
     `TransportHostStartResult.Failures`（地址 + `SocketError` + 消息），其余照常监听；
     一个都没听上时 `IsListening == false` 且**不抛异常**（本机没合格 RFC1918 网卡时是预期状态）。
   - **前置实测（triage B-23）已完成**，结论见下面「M3 已实测记录」与 **ADR-032**：
     「同端口 + 不同具体地址」可以 bind（开不开 `ExclusiveAddressUse` 都行）——这是本步骤成立的前提。
9. ✅ **已完成** accept 之后**先做同子网校验**：用 `client.Client.LocalEndPoint.Address`
   （即接受它的那个 listener 的地址）调 `ISubnetPolicy.IsAllowedPeer(local, remote)`，不过立即关闭。
   顺序写死在 `TransportHost.HandleAsync`：accept → 同子网 → **准入** → TLS，不可调换。
   验收：`Rejects_Peer_That_Fails_Subnet_Check_Before_Any_Tls`（处理器根本不会被调用、
   `AdmittedConnections == 0`）；另有 `Real_SubnetPolicy_Rejects_Loopback_Peer` 证明
   127/8 不是 RFC1918 时真策略也会拒。**已做变异验证**：把子网校验短路后 4 个用例失败。
10. ✅ **已完成** 准入限额在 accept 之后、**TLS 握手之前**占用（`ConnectionAdmissionLimiter`）：
    全局上限 + 更小的每源 IP 上限，`finally` 里释放，租约释放**幂等**。
    验收：`Admission_Limit_Refuses_The_Extra_Connection`（第二条连接拿不到名额、拿不到 TLS）。
    （`TcpListener.Start(backlog)` 的 backlog 只是待 accept 队列，**不是**应用层 DoS 防线——已写进注释。）
11. ✅ **已完成** 有界连接登记表 `ConnectionRegistry`：停机先取消、再强制释放 socket、再 join，
    两阶段都在停机预算内。验收：`Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission`
    ——3 个客户端卡在握手里触发 stop，全部 handler 结束、限额归零、`StopAsync` 返回 true。
    **阶段 2 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **495 PASS / 0 FAIL**
    （阶段 1 的 464 + 新增 31）。

### 阶段 3 —— TLS

12. ✅ **已完成** 服务端显式设置（`TransportHost.CreateServerOptions`）：
    `EnabledSslProtocols = Tls12 | Tls13`、`AllowTlsResume = false`、`AllowRenegotiation = false`、
    `ClientCertificateRequired = false`、`CertificateRevocationCheckMode = NoCheck`；
    客户端 `TlsClientConnector` 同样显式关 resume / renegotiation。
    （默认值实测：`AllowTlsResume` 两端都 True；`AllowRenegotiation` 客户端 True / 服务端 False；
    `EnabledSslProtocols` 默认 `None`。三个都不能靠默认。）
13. ✅ **已完成** 客户端校验回调 `PeerCertificateValidator`：仅当**精确 pin 匹配** ∧ 本地不变量全过才接受
    —— leaf 存在 / 32 字节 pin 相等 / ECDSA P-256 / non-CA / KU 含 digitalSignature /
    EKU 含 serverAuth / 有效期当前有效。这些是**不变量检查不是独立安全边界**；
    注入 `TimeProvider` 测试过期证书（不许改系统时钟）。拒绝码只进本地日志，**绝不下发给对端**
    （否则等于免费探测探针）。
14. ✅ **已完成** **阶段绝对 deadline**（`FrameReader` + `TransportTimeouts`），不是"距上次读到字节 N 秒"：
    TCP connect / TLS 握手 / 4 字节帧头 / 整帧 payload / 首个 hello 各自独立、各自重新计时；
    异步读不靠 `ReadTimeout`（它只覆盖同步读）。
    - 实现要点：`ReadExactlyAsync` 在**每一段进入时**建一个 `CancelAfter` 的 CTS，
      循环里**不再重置**——这就是"绝对"的全部内容。
    - 验收：`Slow_Trickle_Does_Not_Extend_The_Absolute_Deadline`（8 字节 × 150 ms vs 400 ms 时限）。
      **已做变异验证**：把 `CancelAfter` 挪进循环变成滑动窗口后，该用例**变红**
      （滑动窗口版把 8 字节全读完了、耗时 1 s、根本不抛异常）。
    - 顺带把**步骤 16 的长度校验**也做完并落在这里（`FrameReader.TryValidateLength`）：
      `uint` 域内拒 0、拒超上限，**之后**才转 `int`；超限**不 drain**、不分配 payload。
      边界用例：`0 / 1 / 上限 / 上限+1 / 0x7fffffff / 0x80000000 / 0xffffffff`。
    - 新增常量 `TransportConstants.MaxPreAuthMessageBytes = 4 KiB`（评审 A-9）：
      规格里 1 MiB 是**认证之后**的额度，未认证连接不该能逼我们分配 1 MiB。
15. ✅ **已完成** 实测取消延迟与异常形态（triage B-24 / B-25）——**数据是跑出来的，不是推断的**，
    详见下面「M3 已实测记录」与新增用例 `PreAuthDeadlineTests`。
    五个场景各自断言 handler 终止、`ActiveConnections == 0`、`AdmittedConnections == 0`。
    - **握手卡死**这条**已做变异验证**：注释掉 `handshakeCts.CancelAfter(...)` 后，
      名额 12 s 都还不上，用例变红——证明它测的确实是时限而不是碰巧结束。
    - **本步骤只量了「时限执行得准不准」，没有量「五个数值本身合不合适」**。
      数值仍留给第二轮外部评审，不在本机实测范围。
    - 顺手修正了一处会误导后续实现的注释：`TransportHost.HandleAsync` 的
      `catch (OperationCanceledException)` 原先只写「停机取消」，但它同时也是**握手时限到点**的落点
      （卡住的是未认证连接，属于预期防御）。当前处理相同（直接断开），
      **阶段 4 引入会话状态机后应当能被区分**，届时再拆。
    **阶段 3 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **513 PASS / 0 FAIL**
    （阶段 2 的 495 + 新增 18：14 条 `FrameReaderTests` + 4 条 `PreAuthDeadlineTests`）。
    **Last code commit：`5ba5822`** · **Working tree at validation: clean**

### 阶段 4 —— Framing / hello / 状态机

16. ✅ **已完成**（在阶段 3 随 `FrameReader.TryValidateLength` 一并落地）：`uint` 校验 → 拒绝 `0` →
    拒绝 `> 当前阶段上限` → **之后**才转 `int`；超限不 drain、不分配。边界长度
    `0 / 1 / 上限 / 上限+1 / 0x7fffffff / 0x80000000 / 0xffffffff` 全部有测试。
17. ✅ **已完成** pre-auth 独立小上限：验收 `Pre_Auth_Frame_Larger_Than_4KiB_Is_Rejected`
    —— 客户端只发一个声称 64 KiB 的长度前缀，服务端断在 `pre-auth-frame:length-exceeds-limit`，
    且 `FrameReaderTests` 用计数器 + 流位置两把尺子证明**只读了 4 字节、没分配 payload**。
    **已做变异验证**：把它换成认证后的 `MaxControlMessageBytes` 后，该用例 720 ms 内以
    `Expected: pre-auth-frame:length-exceeds-limit / Actual: pre-auth-timeout` 变红。
18. ✅ **已完成** 严格 JSON 解析器（`HelloFrame`）：`AllowDuplicateProperties=false`、
    `UnmappedMemberHandling=Disallow`、显式 `MaxDepth=4`（属性值默认读出 `0` = 内置 64，
    不写就等于没有主张）、拒绝注释与尾逗号、`PropertyNameCaseInsensitive=false`、
    拒绝 UTF-8 BOM、非法 UTF-8 **不做替换字符兜底**。
    另外单做了一步「整段字节必须**恰好**是一个 JSON 值」的检查（`Utf8JsonReader` 先探一次，
    尾随非空白一律 `hello-trailing-data`）——`JsonSerializer.Deserialize` 本身不保证这一点。
19. ✅ **已完成** 首帧必须**且仅能**是 `{type:"channel_hello", channel:"control", protocol:1}`
    （`HelloFrameTests` 44 条 + `First_Frame_Must_Be_Exactly_The_Hello` 5 条真链路）。
    「第二个 hello」用「同一会话 `RunAsync` 只允许一次」在类型层面堵掉。
    **已做变异验证**：让 `TryParse` 无条件返回 true 后，**43 条测试变红**。
20. ✅ **已完成** 状态机 `ControlSessionState`：`AwaitingHello` →（hello 通过）→ `PreAuthenticated`，
    任何失败路径 → `Closed`。「此后一律拒绝」的可执行形式是
    `AllowedOperationsWhilePreAuthenticated` **空集合**，并由
    `PreAuthenticated_Allows_Nothing_Before_M4` 盯着——M4 落地前谁往里加东西谁红。
21. ✅ **已完成** M3 终态选**干净关闭**（`SslStream.ShutdownAsync()` 发 close_notify 后由 Host 释放），
    **没有**启用「进入 AwaitingAuthentication + 占位 deadline」那条备选——
    M3 里没有任何东西值得为未认证连接继续留着它。

    #### ⚠️ 阶段 4 变异验证逼出来的一处真实缺陷（已修）

    第一版 `ControlPreAuthSession.RunAsync` 只接了 `FrameProtocolException` / `EndOfStreamException` /
    `IOException`，**没接阶段超时的 `OperationCanceledException`**。后果是：
    超时会一路飞出 `RunAsync`，`ControlPreAuthResult` **永远产生不出来**，
    而且和「停机取消」在调用方看来完全一样。做 A-9 变异时它表现为「30 秒没有结果」，
    而不是一条可读的断言——这个值 30 秒正是发现它的线索。
    修法：加 `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)`
    → 产生 `pre-auth-timeout`；停机取消继续向上抛（那不是对端的错，不该记成拒绝原因）。
    修完同一变异变为 **720 ms 内以可读断言失败**。新增 `Idle_Client_Is_Rejected_With_A_Timeout_Reason` 固化。

    **阶段 4 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **568 PASS / 0 FAIL**
    （阶段 3 的 513 + 新增 55：44 条 `HelloFrameTests` + 11 条 `ControlPreAuthSessionTests`）。
    **Last code commit：`e0484ec`** · **Working tree at validation: clean**

### 阶段 5 —— 收口

22. ✅ **已完成** triage A 桶 18 条逐条对账（`docs/M3_REVIEW_TRIAGE.md` §2）。
    对账时发现 **3 个真实缺口**（A-6 的本地地址来源、A-16/17 的 TLS 选项、A-1 的终态 e2e），已补。

    | A# | 约束 | 落在哪个测试 |
    | --- | --- | --- |
    | 1 | TLS 成功 = 显式 `PreAuthenticated`，不是"已认证" | `PreAuthenticated_Allows_Nothing_Before_M4`、`Valid_Hello_Reaches_PreAuthenticated_And_Closes`、`After_Hello_The_Connection_Is_Closed_And_A_Second_Hello_Gets_Nothing` |
    | 2 | 点击时冻结不可变快照，握手期间不回读缓存 | `Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`、`TryCreate_FromDiscoveredDevice_FreezesAddressAndPin`、`ExpectedCertSha256_IsNotSharedWithCaller` |
    | 3 | pin 先解码成恰好 32 字节，比较用 `FixedTimeEquals` | `TryDecode_*`、`Matches_*`、`TryCreate_RejectsMalformedPin`、`TryCreate_RejectsPresentedWithWrongLength` |
    | 4 | 同 deviceId 不同指纹不得静默覆盖 | **ADR-027 有意推迟到 M4 之前**，M3 靠第 2 条止血；**本条在 M3 故意没有测试** |
    | 5 | M3 暴露不可变 `PresentedCertSha256`（ADR-028） | `Connect_Succeeds_And_Captures_Presented_Pin`、`TryCreate_CapturesExpectedAndPresented`、`Identity_IsNotAffectedByMutatingThePresentedArrayAfterCreation` |
    | 6 | 同子网在 accept 后、TLS 前，用<b>接受它的那个 listener 地址</b> | `Rejects_Peer_That_Fails_Subnet_Check_Before_Any_Tls`、`Real_SubnetPolicy_Rejects_Loopback_Peer_...`、**新增** `Subnet_Check_Receives_The_Accepting_Listener_Address` |
    | 7 | 准入在 accept 后、握手前；`finally` 释放 | `Admission_Limit_Refuses_The_Extra_Connection`、`Acquires_Up_To_Global_Limit_Then_Refuses`、`Refuses_Same_Address_Beyond_Per_Address_Limit_...`、`Release_Is_Idempotent` |
    | 8 | 阶段绝对 deadline | `Slow_Trickle_Does_Not_Extend_The_Absolute_Deadline`、`Prefix_And_Payload_Deadlines_Are_Independent`、`Handshake_Deadline_Fires_Even_When_Peer_Keeps_Silence`、`PreAuthDeadlineTests` ×4 |
    | 9 | pre-auth 独立小上限 | `Pre_Auth_Frame_Larger_Than_4KiB_Is_Rejected` |
    | 10 | `uint` 先校验，超限不 drain | `Validate_Length_Covers_The_Interesting_Boundaries`、`Oversized_Length_Is_Rejected_Without_Draining_The_Payload` |
    | 11 | 严格 JSON 解析器 | `HelloFrameTests` ×44 |
    | 12 | 首帧必须且仅能是 hello | `First_Frame_Must_Be_Exactly_The_Hello` ×5、`A_Session_Cannot_Be_Run_Twice` |
    | 13 | M3 必须有明确终态 | `Valid_Hello_Reaches_PreAuthenticated_And_Closes`、`After_Hello_...` |
    | 14 | pin 是唯一边界；其余是不变量 | `Rejects_Pin_Mismatch`、`Rejects_Ca_Certificate`、`Rejects_Rsa_Key`、`Rejects_Key_Usage_Without_Digital_Signature`、`Rejects_Enhanced_Key_Usage_Without_Server_Auth`、`Rejects_Missing_*` |
    | 15 | 有效期必须查，用注入时钟 | `Rejects_Expired_Certificate_Using_Injected_Clock`、`Rejects_Not_Yet_Valid_Certificate_Using_Injected_Clock`、`Accepts_Certificate_That_Expires_One_Second_Later` |
    | 16/17 | 两端显式 `AllowTlsResume=false` / `AllowRenegotiation=false` | **新增** `TlsOptionHardeningTests` ×3（`Defaults_Are_What_We_Must_Not_Rely_On` 把默认值也一起钉住，作为"为什么必须显式写"的证据） |
    | 18 | 连接任务有界 + 停机 join | `Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission`、`Stop_Cancels_Ignored_Cancellation_And_Still_Joins`、`Stop_Marks_Stopping_And_Refuses_New_Registrations` |

    为了让 A-16/17 可测，把两端的选项构造提成 `internal`
    （`TlsClientConnector.CreateClientOptions` / `TransportHost.CreateServerOptions`），
    并对 `LanRemote.Transport.Tests` 开 `InternalsVisibleTo`——
    **不这么做的话，删掉一行 `false` 不会有任何东西变红**。
    已变异验证：注释掉客户端的 `AllowTlsResume = false` 后该用例立即变红。
23. ✅ **已完成** `dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **573 PASS / 0 FAIL**
    （阶段 4 的 568 + 新增 5）。HANDOFF 与 `docs/DECISIONS.md`（ADR-033）已更新、变更已提交。
    **Last code commit：`5bf3cb6`** · **Working tree at validation: clean**
24. 🟡 **验收物料已就绪，等用户执行**：两机验收（跨机真实 TLS + pinning）。
    **M3 在用户回填之前不算做完**，本机没有 RFC1918 网卡无法自证。

    **关键结论：`LanRemote.App` 不能用来验 M3。** 它只是 `ProjectReference` 了
    `LanRemote.Transport`，`src/LanRemote.App/` 里 grep
    `TransportHost|TlsClientConnector|ControlPreAuthSession` **零命中**——
    打 App 的包去两机跑，只能重新证明 M2 的 discovery 还能用。

    因此新建 `tools/LanRemote.Acceptance`（`net10.0-windows`，**WinExe + WPF 窗口程序**，已加进 sln）：

    | 文件 | 作用 |
    | --- | --- |
    | `AcceptanceContext.cs` | 刻意照抄 `App.xaml.cs` 的 DI 接线（vault→证书→身份→binding→SubnetPolicy→discovery）；`IAsyncDisposable`，停机时 `StopAsync` 发现服务 |
    | `AcceptanceLog.cs` | WinExe 没有控制台 → 双写 UI（`LineWritten` 事件）+ 磁盘；**每次运行一个不可变文件** `m3-<role>-<UTC>-<runId>.log` |
    | `AcceptanceOutcome.cs` | 五类结局，**枚举值即退出码**；`Combine()` 带优先级 |
    | `AcceptanceRun.cs` | 一轮的上下文：runId / 角色 / 日志 / 中止标记 / 后台故障；头和尾由它写 |
    | `AcceptanceProfile.cs` | **两端共用**的五个 deadline 与各场景期望窗口（写在一处，避免两侧各写一份慢慢漂移） |
    | `AcceptanceLoggerProvider.cs` | 把产品 `ILogger` 事件转发进验收日志（WinExe 下 `AddSimpleConsole` 是死信投递） |
    | `App.xaml(.cs)` | 两个入口（无参开窗 / `--headless`）+ 三个异常 handler（见下） |
    | `MainWindow.xaml(.cs)` | 窗口：身份面板 + **角色锁** + 结论横幅 + 日志区 + 复制/打开目录 |
    | `HeadlessCommand.cs` / `HeadlessRunner.cs` | headless 参数解析与调度 |
    | `InfoRole.cs` | 环境自检，回填身份/指纹/端口/合格网卡 |
    | `HostRole.cs` | 被控端：起 discovery + `TransportHost`，每条连接跑完整 pre-auth 会话，**互斥终态桶** |
    | `ClientRole.cs` | 控制端：发现→冻结快照→TLS→hello→等对端收尾，跑完四个必做场景后打**交叉核对清单** |

    退出码改为 **0 PASS / 1 FAIL / 2 UNMET / 3 HARNESS_ERROR / 4 INVALID_RUN**，
    窗口把它翻译成人话显示。

    **双击 `LanRemote.Acceptance.exe` = 一个窗口，不需要任何脚本。**
    同一个 exe 带 `--headless` 就是命令行模式，**两条路调用完全相同的 `HostRole`/`ClientRole`**。

    **四个必做场景**（`ClientRole.MandatoryScenarios`）：

    | 场景 | 通过标准 | 状态 |
    | --- | --- | --- |
    | `success` | 控制端 `presentedPin` 与 discovery 的 `pin` 逐字符一致 + `peerClosed=eof` 且 `closedAtMs ≤ 3000`；被控端须有 `outcome=PreAuthenticated rejection=-` | 待真机 |
    | `pin-mismatch` | 抛 `AuthenticationException` **且**消息里短码恰为 `pin-mismatch`（不是"抛了认证异常就算"） | 待真机 |
    | `timeout` | 不发 hello，约 5 s（= `lengthPrefixTimeout`）被切；被控端 `rejection=pre-auth-timeout` | 待真机 |
    | `slow-dribble` | 2 s 间隔滴流长度前缀 → **只发出 3/4 字节**就被切在绝对时限上 | 待真机 |
    | `cross-subnet` | —— | **本次不跑，见 §14 第 13 条** |

    **本机已实测（真实执行，不是推演）：**

    - `dotnet build` 0 警告 0 错误、`dotnet test` **574 PASS / 0 FAIL**
    - WPF 窗口**真的渲染出来**（`[GUI] 窗口渲染完成。 ActualWidth=900 ActualHeight=700`），
      已截图肉眼确认四个区块与身份面板内容正确
    - **全新解压 + `env -u DOTNET_ROOT -u DOTNET_HOST_PATH`** 下双击 `LanRemote.Acceptance.exe`
      → 窗口正常打开，自包含成立、也不需要任何脚本
    - 自检真跑通 DPAPI→身份→证书整条链（deviceCode `M5WC-14GX`，
      certSha256 `89A5C10E…5445`），正确判 `outcome=FAIL reason=no-qualified-rfc1918-nic`，
      并在窗口里把两个角色按钮**置灰**（防止「点了没反应」）
    - `--headless info` 退 2、`--headless host` 退 2（"没有任何可监听地址"）、
      `--headless client --all` 打环回假被控端 **4/4 PASS 退 0**、参数错误五条路径全部退 3 且**都有可读输出**

    ### 24.1 变异矩阵：11 例，全部符合预期（2026-09-21，环回）

    **这是本轮最重要的一件事**：验收器的判据在真机跑之前必须先在环回上被**看见红过**。
    为此写了一个可切换行为的假被控端（`Mode.Real / Deaf / Resettable` + `AllowAllPolicy` + `StubHost`），
    用**真 exe** 打它：

    | # | 用例 | 变异内容 | 期望 | 实测 |
    | --- | --- | --- | --- | --- |
    | 1 | `real/success` | 真 `ControlPreAuthSession` | 退 0 | ✅ 退 0（43–59 ms 收尾） |
    | 2 | `real/pin-mismatch` | 同上 | 退 0 | ✅ 退 0，`rejection=pin-mismatch` |
    | 3 | `real/timeout` | 同上 | 退 0 | ✅ 退 0（4982 ms） |
    | 4 | `real/slow-dribble` | 同上 | 退 0 | ✅ 退 0（`sent=3/4`，4976 ms） |
    | 5 | `real/pin-mismatch-doubleflip` | 故意把**已翻转**的 pin 再给一次 | 退 1 | ✅ 退 1（证明「翻转一位」是**承重的**） |
    | 6 | `deaf/success` | 握完手**一个字节都不读**，等满时限才关 | **退 1** | ✅ 退 1（`reset`，4994 ms） |
    | 7 | `deaf/timeout` | 同上 | 退 0（本来就该等时限） | ✅ 退 0 |
    | 8 | `resettable/slow-dribble` | 把绝对时限改成**每读到字节就重置**的空闲时限 | **退 1** | ✅ 退 1（`sent=4/4`，16054 ms） |
    | 9 | `resettable/timeout` | 同上 | 退 0 | ✅ 退 0 ← **这条证明旧判据是空的** |
    | 10 | `resettable/success` | 同上 | 退 0 | ✅ 退 0 |
    | 11 | `nothing-listening/success` | 端口上什么都不开 | 退 **2**（UNMET，不是 PASS） | ✅ 退 2 |

    三条由此**坐实**的结论：

    - **第 9 条是关键**：`resettable` 下 `timeout` 仍然绿 → 单靠 `timeout` 区分不出
      「绝对时限」和「可重置空闲时限」。**`slow-dribble` 才是那个场景存在的唯一理由**
      （评审第 1 条是真缺陷，不是理论担忧）。
    - **第 6 条允许删掉 TCP 探针**：`deaf/success` 能红，靠的是「收尾时刻贴不贴时限」，
      而这条判据成立的前提是 TCP 真连上了——第 11 条证明「没连上」会落到退 2 而不是退 0。
      于是探针（污染被控端计数、制造 TIME_WAIT）可以删掉，改用更强的结构化断言。
    - **第 5 条保证「删一行不会变红」不会发生**：如果哪天有人把 `FlipOneBit` 的调用删掉，
      第 5 条会和第 2 条一起变红。

    完整输出留档：`%TEMP%\lrmut\matrix-full.txt`（94 行，含每例的 `[CLIENT][RESULT]` 与被控端逐条记录）。

    ### 24.2 交叉核对清单：从「猜出来的数字」改成「可证伪的约束」

    控制端跑完会给被控端列一份核对清单，**它永不宣布里程碑通过**：

    ```
    ① outcome=PreAuthenticated 的行恰好 1 条（只有 success 该走到这）
    ② rejection=pre-auth-timeout 的行恰好 2 条（timeout + slow-dribble）
    ③ 其余任何一行都不得是 PreAuthenticated，也不得是 pre-auth-timeout
    ④ connectionsEnteringSessionHandler 落在 3..4
    ⑤ listenersStoppedCleanly=True 且 activeAtStop=0
    ```

    **④ 为什么是区间**——这里我先写错过一次，被实测抓住：

    我原以为 `pin-mismatch` 死在 TLS 阶段、服务端不会留行，于是断言「应为 3（4 减 1）」。
    实测**是 4**：客户端拒绝证书发的是 TLS alert，而 **TLS 1.3 下服务端在收到该 alert
    之前就已经认为握手完成**，于是它照样进了会话处理器，读到 EOF 后给出
    `rejection=pre-auth-eof`（本机环回实测值）；TLS 1.2 下服务端握手会直接失败、才真的不留行。

    这正是「从症状推断因果」的典型错误：从「客户端看到握手失败」推到「服务端没进会话」，
    中间那一步（TLS 1.3 的半开窗口）被我先入为主地跳过了。
    现在 ④ 只依赖「连接走到了哪一步」（`ReachedWire` / `TlsStageRejection`），**不依赖场景通过与否**——
    否则场景一失败，区间自己就跟着漂，读者会以为区间是实测值。

    ①②③ 在**有场景未通过**时会额外打一行 ⚠ 说明「这些数字是『本该是多少』，不是实测值」。

    ### 24.3 配对方法：4 元组，不是计数相等

    控制端每个场景打 `[CLIENT][CORRELATE] local=<本机IP>:<临时端口> peerHost=<对端IP>:<端口>`，
    被控端每条连接打 `[HOST][RESULT] conn#N peer=<同一个端点>`。
    用 `local` ≡ `peer` **唯一配对**一条连接。

    > 为此给 `TlsConnection` 加了只读属性 `LocalEndPoint`（**不参与任何判定**）。
    > 它不参与判定 ≠ 可以不做对侧断言：只断言「非空」的话，把它实现成
    > `返回任意本地端口` 测试照样绿，而错的配对键会让两份日志配错行——
    > 比没有配对键更坏。所以测试断言的是**同一性**：
    > `TlsClientConnectorTests.Connect_Reports_Local_End_Point_Matching_Server_Observation`
    > 要求客户端自报的本地端点 == 服务端 accept 时看到的远端端点。

    ### 24.4 本轮新抓到的四个真缺陷（都不是模型提出来的，是跑出来的）

    | # | 缺陷 | 现象 | 根因 |
    | --- | --- | --- | --- |
    | 1 | **`--all` 下 `pin-mismatch` 必然 FAIL** | 退 1，信息写着「本该在 TLS 阶段被拒绝，实际握手成功了」，看上去像产品坏了 | 直连模式漏了「翻转一位」，四个场景共用同一个 `--pin` → 拿真指纹去连 |
    | 2 | **交叉核对输出自相矛盾** | 逐条期望写 `pin-mismatch → (无行)`，而 ④ 写 `3..4`（承认可能有幽灵行） | 同一份输出里两句话互相打脸，比两句都错更危险 |
    | 3 | **参数错误静默退出** | `--headless client --bogus` → 退 3、stdout/stderr **均 0 字节**，只有 `crash.log` 里一行 NRE | `HeadlessCommand.TryParse` 在错误分支写 `return true`，调用方按「成功」处理 → `RunHeadless(null)` → NRE → 被顶层兜底吞掉，**错误消息全程丢失** |
    | 4 | **④ 的下界在失败轮次里偏大** | 全部死在 TLS 时仍写 `3..4`，会让人去找不存在的行 | `success/timeout/slow-dribble` 的 TLS 失败分支漏标 `TlsStageRejection` |

    四个的共性：**都不是产品代码错，而是「证据/接口自己说谎」**。
    所以严重性高于普通 bug——验收器的错误会污染结论。

    教训各一条：

    - 同一件事有两处实现时（发现路径 vs 直连路径的翻转），**只改一个**必然出事；
      所以 `FlipOneBit` 现在是唯一实现，两条路共用。
    - 跨工具的隐含契约（「验收器会自己翻一位」）必须写在**两边**的注释里，
      否则改一边会以「看起来像产品坏了」的方式炸（变异矩阵的 `real/pin-mismatch` 就是这样被炸出来的）。
    - **返回值的语义必须和调用方的理解一致**。`TryParse` 那个 `return true` 极可能当时想的是
      "这确实是一次 headless 调用"——但调用方按字面意思读。这种错**不可能**被单元测试发现
      （因为两边各自自洽），只能靠端到端跑一遍、并且**看输出字节数**。

    ### 24.5 三个「待实测」项已全部实测收口

    评审把三条 .NET 行为标成「不得据模型结论下判断」，现已在本机测完（详见
    `docs/M3_ACCEPTANCE_UI_REVIEW_TRIAGE.md` §2）：

    | 评审断言 | 实测结论 |
    | --- | --- |
    | 裸后台线程异常不会被 `DispatcherUnhandledException` 转发 | **成立且更极端**：进程**当场死亡**（exit 127，无任何输出，handler 来不及做什么） |
    | `TaskScheduler.UnobservedTaskException` 默认不终止进程 | **成立**：进程存活且**完全静默**（.NET Core 起终结器不再杀进程） |
    | TCP 未连通时不会走到证书校验 | **成立**：抛 `SocketException` / `IOException`（内层 `SocketException`），**永不**是 `AuthenticationException` → TCP 探针删除的依据 |
    | `ReadAsync == 0` 不等于 `close_notify` | **成立**：裸 TCP FIN 也返回 0（RST 抛 `IOException`）；`SslStream.ShutdownAsync` 只有显式调用时才发 `close_notify` → 全部文案降级为「**有序 EOF**（TLS 记录层 EOF）」 |

    两个 handler（`AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`）
    与 `AcceptanceRun.ReportBackgroundFault` 就是按这两条实测结论加的。
    `MainWindow` 的点击处理器是 `async void`、`ClientRole` 里没有 `ConfigureAwait(false)`——
    所以 headless 走 `Task.Run`，绝不让测量被 UI 线程污染。

    ### 24.6 本轮提交与打包记账

    本轮（4 个真缺陷 + headless 入口 + 4 元组配对 + 变异矩阵）提交为 **`f080581`**
    （24 files changed, +3399 −583）。内容清单：

    | 区域 | 文件 |
    | --- | --- |
    | 产品（唯一改动，只读观测量） | `src/LanRemote.Transport/TlsConnection.cs`（`LocalEndPoint`） |
    | 测试 | `tests/LanRemote.Transport.Tests/TestTlsServer.cs`（记录 accept 远端端点）、`TlsClientConnectorTests.cs`（对侧一致性 + Dispose 后为 null） |
    | 验收器 | `tools/LanRemote.Acceptance/`：新增 `HeadlessCommand.cs` / `HeadlessRunner.cs` / `AcceptanceOutcome.cs` / `AcceptanceProfile.cs` / `AcceptanceRun.cs` / `AcceptanceLoggerProvider.cs`，改 `App.xaml(.cs)` / `ClientRole.cs` / `HostRole.cs` / `InfoRole.cs` / `MainWindow.xaml(.cs)` / `AcceptanceContext.cs` / `AcceptanceLog.cs` |
    | 文档 | `docs/DECISIONS.md`（ADR-034）、`docs/M3_ACCEPTANCE_UI_REVIEW_{PROMPT,TRIAGE}.md`、`docs/M3_TWO_MACHINE_ACCEPTANCE.md`、`README.md`、`HANDOFF.md` |

    **打包从 `f080581` 之后的工作树做**（源码与验收器已全部提交，脚本会重新
    `dotnet publish`）。zip 与 `artifacts/` 都在 `.gitignore` 里，不入库、不追溯。
    本文件（`HANDOFF.md`）与其后的记账性修订属文档提交，**不改变代码内容**。

    **本次打包实测**（2026-09-21 16:40，`f080581` 之后的工作树）：
    `265 files packed / raw 132.3 MiB / zip 57.4 MiB` →
    `LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`。zip 校验：265 条目**全扁平**（无子目录）、
    无 `START.cmd`、唯一脚本是 `set-lab-ip.ps1`（UTF-8 with BOM，22304 B）、
    `START-HERE.md` 与 `docs/M3_TWO_MACHINE_ACCEPTANCE.md` **逐字节相同**
    （22197 B，sha256 前 16 位 `cda95eb812351b6b`）。

    **`LANREMOTE_M3_CLEAN=1` 在本机不可用**：脚本内部的 `shutil.rmtree` 同样被 safe-delete
    hook 拦下（`SAFE_DELETE_BULK_CONFIRM_REQUIRED count=273 threshold=50`）→ 脚本死于
    SystemExit。**不要清目录**：`dotnet publish -o` 本来就会覆盖它自己产出的每个文件；
    `artifacts/m3-acceptance/` 里那 13 个**空的**语言卫星目录（`cs/` `zh-Hans/` …）是历史残留，
    `os.walk` 只收文件，**不会进 zip**（上面 265 条扁平条目的实测即为证据）。

    ### 24.7 第二个 lab 准备缺口：TCP 45873 没有任何入站放行（2026-09-21）

    在 A 机（本机）准备 M3 验收环境时抓到的：`set-lab-ip.ps1` 只建了
    `LanRemote Discovery UDP 45872` 一条入站规则，**TCP 45873 一条都没有**。
    Windows 防火墙对入站默认拒绝 → 被控端静默丢掉对端的 SYN，控制端只看得到
    `SocketException stage=tcp`（"连接被拒/超时"），**日志里没有任何线索指向防火墙**。
    M2.1 的发现验收只用 UDP 45872，所以这个缺口一直没暴露；**只有 M3 才需要 TCP**。

    真正的坑不在「少一条规则」，而在**幂等分支**：原脚本发现 lab 地址已存在就
    直接 `exit 0`，根本走不到防火墙步骤 —— 也就是说**重跑脚本永远补不上规则**，
    唯一出路是 `-Undo` 再来一遍。已修：

    | 改动 | 说明 |
    | --- | --- |
    | 提取 `Set-LabNetworkProfileAndFirewall` | profile 重试 + 两条规则的查/建，一处实现 |
    | 幂等分支也调用它 | 「地址已存在」≠「环境已就绪」，地址、profile、规则是三件事 |
    | 规则改两条 | `LanRemote Discovery UDP 45872` + `LanRemote Control TCP 45873` |
    | `-Undo` 一并删规则 | 脚本动过的东西都能精确撤销 |

    **实测证据（A 机，提权运行）**：
    - 第一次运行：建 `192.168.1.10/24`（保留 `172.100.166.220`）+ `profile = Private`
      + `firewall rule already exists`（UDP）；
    - 第二次运行（幂等路径）：`192.168.1.10 already present - address left untouched.`
      → `rule exists : LanRemote Discovery UDP 45872` /
      **`rule created: LanRemote Control TCP 45873`**；
    - 随后 `--headless host --seconds 8`：`[HOST] boundAddresses = 192.168.1.10`、
      `[HOST] port = 45873`、`[HOST][SUMMARY] connectionsEnteringSessionHandler=0
      listenersStoppedCleanly=True activeAtStop=0 handlerFaults=0`、**退 0**；
      同一轮发现服务也起来了（`局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10`）。
    - 脚本语法用 PS 解析器检查：`errors=0`；BOM 保留（24660 字节）。
    - 第二次打包（含本修正）：**265 files / raw 132.3 MiB / zip 57.4 MiB**，
      zip sha256 前 16 位 `234d0b5aebcd6798`，`START-HERE.md` 与手册仍逐字节相同。

    **A 机现场数据（2026-09-21，交叉核对时对表用）**：

    | 项 | 值 |
    | --- | --- |
    | 设备名 | `DESKTOP-D132BMD` |
    | 设备码 | `M5WC-14GX` |
    | 证书指纹 | `89A5C10E8C1950CB2C840F3054CB358274C41B71B9BECC89C8BBA5425320F445` |
    | lab 地址 | `192.168.1.10`（原 `172.100.166.220/24` 保留，未断网） |
    | 监听 | TCP 45873（`boundAddresses = 192.168.1.10`），UDP 45872 发现已启 |

    **提权路径笔记**：PowerShell 的 `Start-Process -Verb RunAs` 被本机安全策略拦
    （"spawns a child process that bypasses PowerShell command validation"）；
    `powershell.exe` 也不能从 Git Bash 直接调。可行路径是 **Python + `ctypes` 直调
    `ShellExecuteExW(lpVerb="runas")`**，拿 `hProcess` 后 `WaitForSingleObject` 等结束、
    `GetExitCodeProcess` 读退出码 —— 本机已实测（两次提权运行都返回 0）。

    **物料**：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`，由
    `scripts/acceptance/make-m3-package.py` 产出
    （`LANREMOTE_M3_SKIP_PUBLISH=1` 跳过 publish 只重打包；`LANREMOTE_M3_CLEAN=1` 才先清空目录）。
    包内含 `START-HERE.md`（=`docs/M3_TWO_MACHINE_ACCEPTANCE.md`）与 `set-lab-ip.ps1`，
    其余是自包含运行时。`set-lab-ip.ps1` 打包时强制加 BOM（PS 5.1 否则中文乱码）。
    手册：§0.1 双击启动、§0.2 headless 用法与退出码、§3 逐场景判定（含 `slow-dribble`）、
    §4 汇总与五条约束、§5 配对方法、§6 两个已知缺口、§7 要贴回来的证据、§8 排障表。

    ### 24.8 M3 第 24 步 · 续「一键准备本机」（2026-09-21）

    **起因**：用户第三次指出「这程序不是可以管理员模式打开吗，怎么还要求手动跑脚本」。
    核对后确认验收器**当时确实没有配置能力**——是功能没做，不是权限问题。用户裁定
    「等一键做好再用」。本小节 = 补齐过程与同轮抓到的 3 个真缺陷；落地 ADR-035
    （主进程永不提权，提权只在窄域 helper）。

    **功能面**（全部在 `tools/LanRemote.Acceptance/` 与 `set-lab-ip.ps1`，`src/` 一行未动）：

    | 组件 | 内容 |
    | --- | --- |
    | `LabAction.cs` | 三个动作（`ApplyA` / `ApplyB` / `Undo`）的枚举与角色映射 |
    | `LabWorkerRole.cs` | **提升实例专用**动词 `lab-apply` / `lab-undo`——只做「跑包内脚本 + 写运行尾」；脚本路径 TOCTOU 重校验（必须在 exe 目录内、非 ReparsePoint） |
    | `ElevatedLauncher.cs` | `ShellExecuteExW(runas)` + `WaitForSingleObject` + `GetExitCodeProcess`；UAC 拒绝 = `ERROR_CANCELLED`(1223) → `outcome=UNMET reason=uac-declined`（人工取消不是失败，不循环弹窗） |
    | `LabSetupRole.cs` | 编排：提权 → 跑脚本 → 尾随子实例日志（双写证据）→ 状态核验（`已就绪：<IP> 在监听地址里`）→ 结局；窗口按钮与 headless 走同一条路 |
    | 窗口 | 「准备为 A 机（192.168.1.10）」「准备为 B 机（192.168.1.20）」「撤销准备（还原网络设置）」+ 完成后自动重新自检；`_processLog` 恢复 `gui.log` 写渲染行 |
    | headless | 新动词 `prepare-lab`（普通权限可跑）/ `lab-apply` / `lab-undo`（提升专用）；`--log-file` 只属于后两者（提升实例的日志落点由编排者约定） |

    **同轮的 3 个真缺陷**（都是跑出来的，不是模型提出来的）：

    1. **子进程中文乱码**：`[LAB][PS]` 段全变 `U+FFFD`，而 `[LAB][SCRIPT]` 段正常。
       根因 = .NET 10 **没有内置 CP936 解码器**（`Encoding.GetEncoding(936)` 直接抛异常 →
       落 UTF-8 兜底），而 PS 5.1 在 **stdout 是管道**时按控制台代码页（936）编码
       `Write-Host`。修法 = 脚本 v4 在 `[Console]::IsOutputRedirected` 为真时主动把
       `[Console]::OutputEncoding` 切 UTF-8（`Invoke-Netsh` 的显式 936 save/restore
       不受影响）；harness 固定按 UTF-8 解；输出出现 `U+FFFD` 记 WARN（提示包内脚本
       可能不配套）。**顺带纠错**：早先「`GetEncoding(936)` 本机可用」的记账是错的；
       `System.Text.Encoding.CodePages.dll` 也不是陈旧残留，而是运行时 framework 资产
       （deps.json 里 `assemblyVersion 10.0.0.0` 的 secondary `"runtime": {}` 条目，
       无 csproj 引用）。
    2. **尾随转发静默丢 7 行**（子实例 90 行 vs 转发 83 行；Python 逐行差集证明缺的是
       adapter / profile / 两条 rule / 一条分隔线 / `[SCRIPT]` 一行）。根因 = tailer 的
       `usable` 越过「文件以换行结尾」的空占位 → 下一行恰好占用该位置 → **永不转发**。
       修法 = 修 `Flush` 记账（`usable` 必须 = 确定写完的行数），并加**独立对账**
       `CountCompleteLines`（数 `\n`，与 `Split` 是两条独立算法）：每次 prepare 打
       `[LAB] 转发对账 = X 行 / Y 行（一致）`，不一致就建议以子实例自己那份为准。
    3. **`gui.log` 的问题不是「丢了」而是「文档还在说双写」**：改 per-run 文件后
       「窗口渲染完成」只进了 UI，「窗口没崩」的证据带不走（排查 GUI 冒烟时被 7 小时前的
       遗留文件带偏过一次）。修法 = `MainWindow` 新增 `_processLog`（`gui.log` 重新写
       渲染行），并把「形态教训」第 3 条与 UI 评审 prompt 的口径一起更正。

    **实测（真升级路径）**：

    - `--headless lab-apply`（幂等复跑）：`elevated=yes` / `scriptExit=0` /
      `stateCheck=已就绪：192.168.1.10 在监听地址里（当前 192.168.1.10）` /
      `keeping the saved pre-lab state`（快照未被污染）→ **PASS**。
    - `--headless prepare-lab` 全流程：提权 → 尾随 → 双份证据 →
      `[LAB] 转发对账 = 提升实例 89 行 / 已转发 89 行（一致）` → 状态核验 → **退 0**。
    - GUI 冒烟：`gui.log` = `[GUI] 窗口渲染完成。 ActualWidth=960 ActualHeight=820`
      （与当前 XAML 一致）、自检 `qualifiedNic=192.168.1.10`、`outcome=PASS`、
      stdout 0 字节、进程存活。
    - `dotnet build` 0 警告 0 错误；`dotnet test` **574 PASS / 0 FAIL**（`src/` 未动）。
    - 重打包：265 files / raw 132.4 MiB / zip 57.4 MiB。**打包后手册又动过一次**
      （净 +6 行 / +535 B：§0.2 的 `prepare-lab` 命令行形态 + `lab-apply`/`lab-undo`
      警示）→ 用 `LANREMOTE_M3_SKIP_PUBLISH=1` 重打刷新 `START-HERE.md`（二进制未变）。
      **最终 zip**：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`，60 197 088 B，
      sha256 `f81d194c6ee4cd6dd5402bfd7085a9e83dd337af267b227b148e2b90c952e1a3`；
      265 条目全扁平、`START-HERE.md` 与手册逐字节相同（25 427 B）、`set-lab-ip.ps1`
      与源文件一致（32 669 B、含 BOM）。

    **提交**：`1d5ffc8`（本小节所述代码与文档都在这一个提交里；HANDOFF 与记忆是其后的
    记账提交）。**B 机换包流程**：拷新 zip → 解压 → 双击 exe → 点「准备为 B 机」→
    一次 UAC → 自检转绿 → 按手册 §3 跑四场景。

    遗留（低优先，不为它单独重打包）：`HostRole.cs` 的前置失败文案仍只写
    「用 `set-lab-ip.ps1`」——窗口路径下该分支基本不可达（自检不合格时角色按钮本来
    就是灰的），下次动代码时顺手改。

### M3 第 24 步 · 两机验收记录（2026-09-21 真机执行 —— **PASS**）

**判定口径**：本轮判定 = **逐条连接证据**（验收手册 §4 的五条约束 + §5 的 4 元组配对，
两份日志对得上）。被控端整轮**结局字段**是 `INVALID_RUN`（退 4）——那是「操作员按了停止」
触发收尾机制后的机械产物，**不是失败、不需要重跑**（来历见下）。

**两台机器与物料**（A = 控制端 / B = 被控端）

| 项 | A 机 | B 机 |
| --- | --- | --- |
| 设备名 / 设备码 | DESKTOP-D132BMD / `M5WC-14GX` | DESKTOP-CU2263D / `3ERD-R74V` |
| lab 地址（唯一合格网卡） | `192.168.1.10` | `192.168.1.20` |
| 设备 id | `fbdf903d-988c-4b38-b999-25998befe71f` | `ee59c0c0-c05c-4034-ba6b-5f1f006c7ae1` |
| 证书指纹 | `89A5C10E…5320F445` | `6755838E…49A73938` |
| osBuild / .NET | 10.0.26200 / 10.0.12 | 10.0.22631 / 10.0.12 |

物料：harness `B0FAF788…BF544A`（162 304 B）、`LanRemote.Transport.dll` `A47E64AF…8BCE2B`
（53 248 B），**两端 `[BUILD]` 行逐字符一致**；执行物料 = zip `f81d194c…`（60 197 088 B / 265 文件）。
跨端互证：A 的 `peerDeviceId` = B 的 `hostDeviceId`；A 的 `presentedPin` 与 B 的
`hostCertSha256` **逐字符相同**；B 的 `[HOST][CORRELATE]` 里 `boundAddresses="192.168.1.20"`
`port=45873` 与 A 每条 `peerHost=192.168.1.20:45873` 对上。

**A 侧**（runId `8a7e3d03`，`startedUtc=10:56:00.039Z`，`--headless client --peer 3ERD-R74V --all`，退 0：4/4 PASS）

| 场景 | 关键实测行（原文摘） |
| --- | --- |
| success | `tls = ok proto=Tls13`；`presentedPin= 6755838E…4938`；`local=192.168.1.10:49575`；`peerClosed = eof t=29 ms` |
| pin-mismatch | `wrongPin` 与真值仅 `byte[16] ^ 0x01` 之差（`…E2B4…`→`…E2B5…`）；`AuthenticationException: 对端证书未通过校验（pin-mismatch）`；**无 `local=` 行**（握手失败前拿不到端点） |
| timeout | `local=192.168.1.10:49577`；`peerClosed = eof t=5013 ms`（长度前缀绝对时限 5000 ms） |
| slow-dribble | `local=192.168.1.10:57131`；`已发 3/4 字节 t=4027 ms`；`peerClosed = eof sent=3/4 t=5000 ms` |

`[VERDICT] M3 = PENDING-HOST-EVIDENCE` 系设计（控制端不单独宣布里程碑）。

**B 侧**（runId `0e7f03d0`）四条连接行与 4 元组配对（唯一配对；配对方法见验收手册 §5）

| B 行（原文摘） | 配对依据 | 认定 |
| --- | --- | --- |
| `conn#1 peer=192.168.1.10:49575 outcome=PreAuthenticated rejection=-` | A `local=…:49575` | success |
| `conn#2 peer=192.168.1.10:49576 rejection=pre-auth-eof`（elapsedMs=3） | **唯一没有对应 `local=` 行的 B 行**；端口 49576 居 49575/49577 之间（顺序分配佐证） | pin-mismatch（TLS 1.3 半开窗口，验收手册 §3 场景 2 的预期形态） |
| `conn#3 peer=192.168.1.10:49577 rejection=pre-auth-timeout elapsedMs=5017` | A `local=…:49577` | timeout |
| `conn#4 peer=192.168.1.10:57131 rejection=pre-auth-timeout elapsedMs=5004` | A `local=…:57131` | slow-dribble |

**五约束核对：全过。** ① PreAuthenticated 恰 1 条；② pre-auth-timeout 恰 2 条（5017 / 5004 ms，
都贴 5 s）；③ 其余唯一一条是 `pre-auth-eof`；④ `connectionsEnteringSessionHandler=4` ∈ 3..4；
⑤ `listenersStoppedCleanly=True activeAtStop=0`。
另：`[HOST][BUCKETS] sessionHandled=4 active=0 sum=4 partitionOk=True {preauthenticated=1, rejected:pre-auth-eof=1, rejected:pre-auth-timeout=2}`、
`handlerFaults=0`、`tlsStageRejections=UNOBSERVABLE`（不填 0）。

**`INVALID_RUN` 的来历（设计使然，防误读）**

- 验收手册 §2 第 4 步要求 B 点「停止监听」收尾；该按钮**无条件**调 `MarkOperatorAbort`
  （`MainWindow.StopHostButton_Click`，注释原文：「停机会强行关掉 socket，而『连接被关闭』
  正是若干场景的通过条件。不整轮作废，就等于用一次按停操作伪造出通过」；`HostRole` 收尾处
  同条件再标一次）→ `Settle()` 机械返回 `InvalidRun` →
  `[RESULT] outcome=INVALID_RUN / aborted=True / exitCode=4`。
- 所以 **GUI 流程下被控端结局字段必为 `INVALID_RUN`**，证据不受影响。**别重跑、别当缺陷。**
- 替代形态（本机实测）：`--headless host --seconds N` 定时停机不走操作员中止路径——
  `--seconds 5` → `outcome=PASS`、`aborted=False`、退 0（该次 0 条连接，runId `9c4298e1`；
  顺带证明结局字段本来就不承担「场景跑没跑过」的判定）。
- 手册已澄清 4 处（§0.2 退出码表 / §2 第 4 步 / §4 / §8），并顺手修正 §4 两处滞后内容
  （BUCKETS 模板改为代码现状 `sum=… {桶=计数}`；UNOBSERVED 交叉引用 §5.1→§6.1）。
  **执行用 zip 未重打包**——物料必须与真机执行的字节一致；澄清只对日后重跑 / 后续里程碑生效。

**B 机 lab 全流程实测（同一份日志）**：apply ×2（第二次为 undo 后幂等重跑，两条规则均
`rule created`；第一次 `rule exists` + `rule created`）、undo ×1（`removed 192.168.1.20`、
两条规则删除、DHCP 恢复；网络类别保持 Private——ADR-036 既定行为）、info ×4；
`[LAB] 转发对账` 每次一致（129/129、104/104）。首次误操作轮（起监听后立即停止）→
INVALID_RUN 退 4、`sessionHandled=0`，与上述机制一致。

**遗留（文字性，不为它单独重打包）**：A 侧交叉核对清单标题写「下面四条」而实列 5 项
（`ClientRole` 文案）；与 `HostRole.cs` 前置失败文案一起，下次动代码时顺手改。

**收尾（已完成，两机 lab 均已不存在，第 24 步至此全链闭环）**：
B 机 2026-09-21 11:13Z 由用户在窗口点「撤销准备」（`lab-undo` PASS，转发对账 104/104：
`removed 192.168.1.20` + 两条规则删除 + DHCP 恢复）；
A 机 2026-09-21 11:21Z（本地 19:21）由开发侧以管理员身份直跑同物料 `--headless lab-undo`
（runId `037fd836`，`outcome=PASS` / 退 0；**不弹 UAC**——直跑的是提升动词本体，
进程本身已是管理员、没有父进程编排那一层，故没有「转发对账」行）。
A 机脚本自述：`removed 192.168.1.10` / `not present 192.168.1.20` / 两条规则删除 /
`switching interface back to DHCP … lease renewed.`；撤销后以太网 = `172.100.166.220/24`
（Dhcp，续租拿回同地址；非 RFC1918 属正常）。
撤销后两机 `--headless info` 均 `qualifiedNic=(无)` / `UNMET reason=no-qualified-rfc1918-nic`
（A 侧 runId `2f18741c` 退 2；B 侧 `8e702a4a`）。
A 机 undo 日志里的两条提示是**设计内路径**（脚本 v4 头部 revision notes 已预记，非缺陷）：
① `WARNING: saved state points at a lab address (192.168.1.10)`——旧版脚本曾把 lab 状态
写成「原始状态」，v3 起识别毒化状态文件并弃用、走 DHCP 兜底（脚本注释里的 "Measured on
machine A" 说的就是本机）；
② `network profile left as-is (no saved category to restore)`——无可用类别记录时保持
Private（B 机为 `already Private`）。类别还原逻辑自 v3 起存在（有记录才还原），
无记录不还原属设计内兜底——`UX_FIRST_RUN_REVIEW_TRIAGE` §6 已就此闭合（见该文件 §3.3 勘误）。
**本轮文档修订（手册 §1.2 / §9、TRIAGE §3.3 / §6）均不重打包**——执行用 zip 必须与
真机执行的字节一致；包内 `START-HERE.md` 保持打包时快照（25427 B），此后与
`docs/M3_TWO_MACHINE_ACCEPTANCE.md` 不再逐字节相同，属预期，日后重打自然刷新。

### M3 验收器的形态教训（用户连续三次纠错后定稿）

这三条是踩出来的，别改回去：

1. **入口必须是 exe，不是脚本。** 用户原话：「双击入口不应该就是一个exe可执行文件吗？
   为什么要用脚本？我们这个做的是**软件**啊」。本项目是桌面软件，
   M2 的两机验收就是两台机器开 WPF 界面跑的（§9.1「用户在 B 机界面确认」「B 点刷新」），
   §13 又定了「终端用户永远不需要打开 PowerShell」。
   → 验收器是 `WinExe` + `UseWPF`，双击 = 窗口。
2. **控制台 exe + `START.cmd` 是错解**（曾短暂加过，已删除）。
   它把验收又推回终端，而且双击控制台 exe 只会打一行 usage 然后退出，
   窗口一闪而过，用户会得出「包里没有 exe」的结论——用户的第一次反馈
   「你打包解压出来的，怎么没有启动exe」正是这个现象。
3. **`WinExe` 没有控制台**，所以 `Console.WriteLine` 全部不可见 →
   统一走 `AcceptanceLog`：**UI 日志区 + 每轮一个不可变文件**
   （`%TEMP%\lanremote-m3-acceptance\m3-<角色>-<UTC时刻>-<runId>.log`）；
   进程级事实（`[GUI] 窗口渲染完成` 这种「一次进程只有一次」的行）落 `gui.log`。
   另外必须给 `DispatcherUnhandledException` 落盘 `crash.log`，否则未捕获异常只会让窗口无声消失。

   > **更正（2026-09-21，实测发现）**：这一条原先写的是「UI + `gui.log` 双写」。
   > 那是 `f080581` **之前** `AcceptanceLog` 的行为；改成 per-run 文件之后这句话没跟着改，
   > 而「窗口渲染完成」那一行顺势只进了 UI——等于「窗口没崩」这个证据**带不走**
   > （排查 GUI 冒烟时被这个假线索带偏过一次：读到的 `gui.log` 是 7 小时前的遗留文件）。
   > 现在两处都补齐：per-run 文件是主证据，`gui.log` 记进程级事实，渲染行两边都写。

**踩到并修掉的坑：**

- **`InvariantGlobalization=true` 会让 WPF 直接崩**：窗口在首次 Measure 时抛
  `TypeInitializationException: 'MS.Internal.FontCache.MajorLanguages' 的类型初始值设定项引发异常`，
  内层是 `CultureNotFoundException: … 'en' is an invalid culture identifier`。
  WPF 字体缓存必须用真实 `CultureInfo`。**此项目必须 `false`。**
- **顺手纠正一个我写错的猜测**：改 `InvariantGlobalization` **不会**让包多带 ICU——
  Windows 上 .NET 用系统 ICU，发布目录里一个 `icu*` 文件都没有。
  体积从 77.8 → 132.2 MiB 全部来自 WPF 自身的程序集
  （`PresentationFramework` 15.8 MiB + `PresentationCore` 8.3 MiB + WPF 全套约 39 MiB），
  跟 globalization 无关。这就是「先测再写」的例子。
- **`<InvariantGlobalization>` 注释里不能出现 `--->`**：那是 XML 注释结束符，
  直接 `MSB4025: An XML comment cannot contain '--'`。
- **WPF 会带出 13 个语言的资源卫星目录**（`cs`/`de`/…/`zh-Hans`）→
  `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` 限掉，
  条目 422 → 265。配合 `<DebugType>embedded</DebugType>` 不再单独发 pdb。
- **`DispatcherUnhandledException` 处理里必须防重入**：
  若异常发生在排版/重绘路径（如上面的字体缓存崩溃），
  每次重绘都会立刻再抛一次，`MessageBox` 弹 → 重绘 → 再弹 …… 变成弹框风暴，
  用户除了强制结束进程什么都做不了。现在只弹第一次，写盘用 `AppendAllText` 保留全部现场。

    **提交 `5ae052f`** · 该提交只含验收器 / 手册 / 打包脚本 / HANDOFF，**无产品代码改动**
    （`Last code commit` 仍是 `5bf3cb6`）。zip 与 `artifacts/` 都在 .gitignore 里，
    需要时用 `make-m3-package.py` 重现。

### M3 明确不做

- AuthChallenge / HMAC / 访问密钥认证（M4）；视频通道与 Video Attach（M5）。
- UI 网络诊断（ADR-024，M9）、防火墙一键（ADR-025，M10）、临时私有地址一键（ADR-026，不早于 M10）。
- 改动 discovery 缓存语义（ADR-027 规定：M3 不动，最晚 M4 再引入 `IdentityConflict`）。

### 哪些步骤必须本机实测（**不能问模型**）

- 步骤 3、15：握手与取消延迟（本机有 oracle）
- 步骤 8：多网卡同端口 bind
- 步骤 24：跨机真实链路
- **TLS 1.2/1.3 在 Win10 22H2 上的行为本机无法验证**（本机是 Win11 25H2 / 26200）→ 标注未测，别外推

## 16. 下一位 AI 不要重复做

- **M2.1 两机验收已完成并 PASS**（第 9.1 节，20/20，discovery 轮）；**M3 两机验收
  （真实 TLS + pinning）已于 2026-09-21 跑完并 PASS**（第 15 节第 24 步记录）——
  **不要重复跑**，也不要被被控端结局字段 `INVALID_RUN` 迷惑（收尾机制的机械产物，
  判定看逐条连接证据）
- **不要重复做 M2.1 那次两机验收**：2026-09-20 已 PASS（第 9.1 节）。只有改动 discovery
  收发逻辑（网卡筛选 / probe / announce / 缓存 TTL）才需要重跑
- **不要把验收器改回「控制台 exe + 脚本驱动」**：用户已连纠三次。
  入口必须是 exe（双击开窗），headless 只是**同一个 exe** 的另一个入口
- **不要把 probe 回应发回 `remote.Port`**：probe 源端口是随机临时端口，回应必须打到 `remote.Address:45872`
  （实测三次源端口 `50193` / `54670` / `58869`，均为随机端口，回源端口对端必收不到）
- **不要删掉 sender 的 `MulticastInterface` 设置**：不设就会全部走系统默认组播路由（多网卡必踩）
- **不要在 capabilities 里恢复「空项静默跳过」或「按去重后数量判上限」**（两者都是可绕过的校验）
- **不要**为了图省事把 `StartDiscoveryAsync` 改回 `void`、或让 `LoadAsync` 无条件覆盖状态文本
- **不要重新定义** `ISubnetPolicy` / `DiscoveredDevice` / `DeviceIdentity` / `DeviceCode`
- **不要**用字符串前缀或「前三段相同」判断同网段
- **不要**往 announcement 里加地址字段并相信它（地址必须来自 UDP source）
- **不要**为了「支持 VPN 局域网」在 M2 放宽虚拟网卡过滤
- **不要**在 M2 实现任何 TLS / Auth / 视频 / 输入
- **不要在 M3~M8 实现 ADR-024 / 025 / 026**：UI 网络诊断属于 M9、防火墙一键与临时地址一键属于 M10。
  它们都已定好规则，缺的是时机，不是设计——**等用户下达对应里程碑的开工指令**
- **绝不在后台静默改防火墙或改 IP**：两者都必须经过「用户显式点击」+「UAC 确认」两道确认（ADR-025/026）。
  「改 IP 要无感」这句话被拆解为：**入口无感（在 App 内、不用开 PowerShell）+ 触发显式（用户自己点）**，
  不要把「无感」理解成「自动执行」
- **不要靠 `System.Text.Json` 的默认设置解析 `channel_hello`**：实测 `AllowDuplicateProperties` 默认
  **`True`** 且**后者覆盖前者**——`{"type":"channel_hello","type":"video"}` 默认会解析成 `video`。
  hello 解析器必须显式：`AllowDuplicateProperties=false`、`UnmappedMemberHandling=Disallow`、显式 `MaxDepth`
  （注意该属性默认值是 `0`，表示采用内置上限 64，文档里别写成「默认 64」）
- **不要依赖 TLS 相关默认值**：`AllowTlsResume` **两端默认都是 `True`**、`AllowRenegotiation` 客户端默认 `True`、
  `EnabledSslProtocols` 默认 `None`——三个都必须显式设置
- **不要把 `expectedPin` 当作 M4 transcript 的唯一绑定对象**：必须绑定 M3 实际出示的 `presentedPin`（ADR-028），
  否则留下「一条连接收 nonce、另一条连接中继」的凭据中继缺口
- **绝不使用 `EphemeralKeySet` 载入 TLS 服务端证书**（ADR-029，实测 9/9 失败）。
  也不要因为「单元测试没报错」就以为它可用——**同一进程先 `PersistKeySet` 导入过同一私钥后，
  `EphemeralKeySet` 会碰巧成功**（实测污染）。flag 结论必须在新进程、顺序受控下测
- **不要只根据客户端异常判断 TLS 失败原因**：客户端只会看到 `IOException: unexpected EOF`，
  真实原因在**服务端**异常里（`AuthenticationException` + Win32 inner）。spike 必须两边都捕获
- **别忘了 .NET 10 的证书校验回调参数是 `X509Certificate`（基类）**，没有 `RawData` 属性；
  算 pin 要用 `cert.GetRawCertData()`（或转型成 `X509Certificate2`）。编译器会直接报错提醒，别绕过去
- **不要把 `CertificateRequest.CreateSelfSigned(...)` 的结果直接当 TLS 服务端证书**（ADR-030）：
  它的私钥是 ephemeral 的，服务端会在 `AcquireCredentialsHandle` 处失败
  （`... does not support ephemeral keys.` ← `0x8009030E`，与 ADR-029 同一个错）。
  **必须**导出 PFX 后用 `DefaultKeySet` 重新导入——`DeviceCertificateService` 里那步
  PFX 往返是必需环节，不是可以"简化"掉的冗余
- **不要给 `SslClientAuthenticationOptions.TargetHost` 填设备相关的名字**：CN 是
  `LanRemote-<设备码>`，SNI 是明文，等于向整个局域网广播设备标识。pinning 下主机名校验不参与
  安全判定，`TargetHost = string.Empty` 实测可用（不发 SNI、不做名字校验）
- **不要把 `TransportTimeouts` 里的数值当成实测结论**：它们是初始值，步骤 15 实测取消延迟后要回来调。
  ⚠️ 步骤 15 **只验证了「时限执行得准」（0–36 ms 误差）**，**没有**验证「这五个数值合不合适」——
  后者留给第二轮外部评审，别把前者当成后者的结论
- **不要把 `FrameReader.ReadExactlyAsync` 里的 `CancelAfter` 挪进读循环**（评审 A-8）：
  那会把绝对 deadline 变成滑动窗口，被「每 `timeout - ε` 发一字节」无限续命。
  变异已验证：挪进循环后低速攻击用例立刻变红
- **不要按取消异常的子类下断言**：真实 `SslStream` 抛的是基类 `OperationCanceledException`，
  而 `MemoryStream`/`Task.Delay` 替身抛的是 `TaskCanceledException`。
  断言写 `is OperationCanceledException` 即可，写 `IsType<>` 两边必有一边假红
- **不要让阶段超时飞出 `RunAsync`**：它的异常类型与「停机取消」完全相同，
  不接住就等于「结局永远不产生 + 两种取消无法区分」。必须用
  `when (!cancellationToken.IsCancellationRequested)` 分开（阶段 4 已修，见第 15 节）
- **不要在 `JsonSerializer.Deserialize` 之后就以为报文是「恰好一个 JSON 值」**：
  尾随内容不保证被拒。pre-auth 这类输入必须自己先用 `Utf8JsonReader.TrySkip()` 探一次边界
- **不要往 `AllowedOperationsWhilePreAuthenticated` 里加东西**：它在 M3 必须是空集合，
  有测试盯着。真到了 M4，加的应当是「开始访问密钥认证」这一项，而不是「顺手先支持的」能力
- **不要在测试里从 `MemoryStream` 派生并同时重写 `Read(Span<byte>)` 与 `ReadAsync(Memory<byte>)`**：
  一次读会被数**两遍**——`Stream.Read(Span<byte>)` 的默认实现会**虚拟调用** `Read(byte[], int, int)`。
  实测输出 `async=1 span=0 array=1` 却 `BytesRead=8`（真实只消费 4）。要计数就**包装**内部流，
  且用「计数器 + 流自身 Position」两把尺子交叉验证
- **不要声称 `ExclusiveAddressUse` 能发现端口已被占用**（ADR-032，实测证伪）。
  它能挡的只有「带 `SO_REUSEADDR` 的后来者」，而且在本机开不开结果一样。
  真正的冲突信号是 `AddressAlreadyInUse`，已在 `TransportHostStartResult.Failures` 里
- **变异验证不能只做一半**：改完代码必须确认测试**真的变红**再还原。
  绿着不动手很可能是测试隔离错了（阶段 2 就踩过一次，见 ADR-032 末尾）
- **不要用改系统时钟来测证书有效期**：一律用 `TimeProvider` 注入（测试里 `FakeClock`）。
  另外造过期证书时要注意——证书按真实时间也得有效，否则服务端 Schannel 会先因过期自行拒绝，
  测出来的就不是"我们的校验器拒绝了它"
- **不要照抄直觉去改 IP**：Windows IPv4 是「DHCP 或静态」二选一；追加第二地址前必须先把整张接口
  切成静态并回填原配置，否则会丢 DHCP 租约只剩 169.254（本机已两次踩断）。撤销必须幂等，
  且**不能靠 `netsh` 退出码判成败**。完整约束见 ADR-026
- **不要给这个项目做「控制台 exe + 批处理启动器」形态的工具**：
  本项目是桌面软件，M2 验收就是在两台机器的 WPF 界面上做的（§9.1），
  §13 的总原则是「终端用户永远不需要打开 PowerShell」。
  用户已连续三次纠错（「怎么没有启动exe」→「双击入口不应该就是一个exe 」→「我们做的是软件」）。
  验收器必须是 `WinExe` + `UseWPF`，**双击 = 窗口**，不需要任何脚本
- **不要在前面的注释里写没验证过的因果**：我曾断言「关掉 `InvariantGlobalization` 会让包多带 ICU」——
  实测 Windows 上 .NET 用系统 ICU，发布目录一个 `icu*` 都没有；
  体积从 77.8 涨到 132.2 MiB 全部是 WPF 自身程序集（约 39 MiB）。
  写注释前先 `ls` 一下，别把猜测写成事实
- **不要给 WPF 程序开 `InvariantGlobalization`**：窗口会在首次 Measure 时崩在
  `MS.Internal.FontCache.MajorLanguages`（`CultureNotFoundException: … 'en' is an invalid
  culture identifier`）。同时 `DispatcherUnhandledException` 的处理**必须防重入**，
  否则渲染期异常会造成「弹框 → 重绘 → 再弹」风暴，用户连日志都读不到
- **不要忘了 XML 注释里不能出现 `--`**：`<PropertyGroup>` 里的注释写 `--->`（写异常链很自然）
  会直接 `MSB4025: An XML comment cannot contain '--'`，项目都加载不了
- **不要把「对端握手失败」当成 PASS**：端口没开 / 防火墙拦 / host 没启动同样会让握手失败。
  这条空洞断言在 M3 修过两轮：先补了独立 TCP 探针，**「先修再跑」时又删掉**（见 24.1：
  探针自身的判据是空的，还会污染被控端连接计数、制造 TIME_WAIT）。「没测成」与「测出来
  不合格」的分离改由**结局映射**承担：TCP 不可达抛 `SocketException`/`IOException`
  （**永不** `AuthenticationException`）→ 退 `2`；真的连上、死在 TLS 之后才退 `1`
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出
- **验收脚本别再犯这 5 个错**（详见第 9.1 节末表）：`netsh` 退出码不可信、
  `-join` 在 PS 5.1 的参数绑定陷阱、自动选网卡必须排除虚拟网卡、
  Windows IPv4 不支持「DHCP + 附加静态」共存、`check-logs` 必须带 `-Since` 才准
- **别拿 `LanRemote.App` 验收传输层**：它 `ProjectReference` 了 `LanRemote.Transport` 却**零调用**。
  任何「打 App 的包去两机跑」的方案都只能重证 discovery，碰不到 TLS/pinning/hello
- **验收器里禁止「失败即 PASS」**：对端端口没开、防火墙拦掉、host 没启动，都会让握手失败。
  「没连上」**绝不能**报 `0`，它必须落退 `2`（前置条件不满足）；**不要**再为此加独立 TCP
  探针（已删，理由见上一条）。按 `SocketError` 区分「没连上」与「被拒绝」时，
  **不要**把 `ConnectionReset` 归进「没连上」——accept 后立刻关闭正是走 RST
- **不要把「对端怎么收尾」压成一个布尔**：`catch → return true` 会让「读超时」和「被切断」
  在输出里长得一模一样。要区分为 `eof` / `reset` / `still-open` / `unexpected-data` 四种，
  判定标准才写得出来
- **写验收手册前先确认被控端到底会不会打印**：`TransportHost` 无 logger，同子网拒绝 / 准入拒绝 /
  TLS 失败**全是静默**；只有发生在会话处理器**内部**的 pre-auth 超时才有输出。
  手册里不要凭"应该会记日志吧"去写判定标准（见 §14 第 12 条）
- **不要把子进程输出按「OEM 代码页」解**（2026-09-21 实测纠错）：Windows PowerShell 5.1
  在 stdout 是**管道**时按控制台代码页（936）编码 `Write-Host`，而 .NET 10 **没有内置
  CP936 解码器**（`Encoding.GetEncoding(936)` 直接抛；要用得引
  `System.Text.Encoding.CodePages` 包——本项目不引）。现行契约：脚本在
  `[Console]::IsOutputRedirected` 时主动切 UTF-8 输出，harness 固定按 UTF-8 解，
  输出出现 `U+FFFD` 记 WARN。中文权威副本永远看 `[LAB][SCRIPT]` 段
  （脚本自己 `Add-Content -Encoding UTF8` 写 `%TEMP%\lanremote-lab-ip.log`）
- **不要把 `gui.log` 当「每轮日志」**：它是**进程级事实**文件（如
  `[GUI] 窗口渲染完成`）；每轮验收证据在不可变的 per-run 文件
  `m3-<角色>-<UTC>-<runId>.log`。混淆过一次：排查 GUI 冒烟时读到的 `gui.log`
  是 7 小时前的遗留文件，「窗口没崩」的证据其实带不走
- **「按行数转发日志」的照抄陷阱**：tailer 的 `usable` 必须 = 「**确定写完**的行数」——
  `Split('\n')` 的末段永远是特例（空占位或半行）。本工具曾因此**静默丢 7 行**
  （文件正好以换行结尾时 `_emitted` 越过空占位，下一行恰好占用该位置 → 永不转发）。
  必须配**独立算法**对账（`CountCompleteLines` 数 `\n`），聚合计数相等不算证明
- **不要给提升实例动词（`lab-apply` / `lab-undo`）加功能或放宽参数面**：它们要求进程
  本身已是管理员，参数面故意做窄（ADR-035：一次提权一件事、TOCTOU 重校验、UAC 拒绝
  不循环）。要新能力加在编排者一侧（`prepare-lab` / 窗口按钮）

## 17. 关键上下文

1. **`dotnet` 不在 PATH**：先 `source scripts/env.sh`
2. **本机唯一活跃网卡 `172.100.166.220` 不是 RFC1918** → 本机跑不通发现（不是 bug）
3. **模拟「重启」/「新进程」必须新建 `DpapiSecretVault`**（有缓存）
4. **`WatchAsync` 只有 upsert，没有 remove event**；UI 自己按 `LastSeen` prune
5. **`AllowDiscovery=false` 只停 announce 与 probe 响应**，不停扫描、不停接收
6. **`ProbeAsync` 是 M2 唯一新增的接口方法**，不要再加别的
7. **`DiscoveryRuntimeState.Initialize` 之前拿不到快照**（抛异常）——这是隐私保证
8. **`X509Certificate2.NotBefore/NotAfter` 返回本地时间**，与 `DateTime.UtcNow` 比较前要 `.ToUniversalTime()`
9. **`TryDecodeExact` 失败时 out 是 `Array.Empty<byte>()`**
10. **HANDOFF 不写 HEAD hash**，写 `Last code commit` + `Working tree at validation`
11. **probe 回应端口是常量 45872，不是 probe 的源端口**——这是本轮修掉的阻断性 bug，别写回去
12. **`IP_MULTICAST_IF` 只接受网络序的接口 IPv4 地址或 `0.x.x.x` 形式的索引**；
    对地址做 `HostToNetworkOrder` 会抛 `SocketException`（本机实测，见 §1.5.2）
13. **Discovery 项目对 `LanRemote.Protocol.Tests` 开了 `InternalsVisibleTo`**，
    只为两个 internal 纯函数服务；不要顺手把别的内部类型变成测试依赖
14. **ADR-018 曾被错标成 ADR-021**（2026-09-20 已修正）：ADR-016 说的是「证书加载改用 EphemeralKeySet」
    被 ADR-018 取代，而 ADR-021 是另一条「ECDSA 证书 KeyUsage 只允许 digitalSignature」。
    引用时看清条目正文，不要按编号顺序去猜
15. **不要给验收器写「凭症状推断出来的数字」**（2026-09-21）：交叉核对里那个
    「`connectionsEnteringSessionHandler` 应为 3」就是这么写出来的，实测是 4（TLS 1.3 幽灵行）。
    只写**可证伪的约束**，并让区间取决于「连接走到了哪一步」而不是「场景是否通过」。
16. **不要让验收器的返回值语义和调用方的理解相反**（2026-09-21）：
    `HeadlessCommand.TryParse` 曾用 `return true` 表示「参数错误」，调用方按字面读成「成功」，
    于是 `RunHeadless(null)` → NRE → 被顶层兜底成退 3，**错误消息一个字都没打出来**。
    这种错两边各自自洽、单元测试发现不了——只有端到端跑一遍并**核对输出字节数**才看得见。
    现在加了一条铁律：**新增任何 CLI 参数的第一件事，是把错误路径的输出字节数核一遍。**
17. **不要靠「计数相等」配对两条日志**（2026-09-21）：用 4 元组
    （控制端 `local=<IP>:<临时端口>` ≡ 被控端 `peer=<IP>:<端口>`）。
    聚合数相等不构成配对证明。
18. **不要把 `ReadAsync == 0` 说成「对端发了 `close_notify`」**（2026-09-21 实测）：
    裸 TCP FIN 也返回 0。措辞只能是「**有序 EOF（TLS 记录层 EOF）**」。
19. **不要从「客户端看到握手失败」推出「服务端没进会话」**（2026-09-21 实测）：
    TLS 1.3 下服务端可能已经完成握手、照样进会话处理器并给出 `rejection=pre-auth-eof`。
    判定 TLS 失败**永远抓服务端异常**，客户端只有 EOF。
20. **`slow-dribble` 不能省**：`timeout` 单独区分不出「绝对时限」和「可重置空闲时限」
    （变异矩阵第 9 例实测：`resettable` 下 `timeout` 仍然绿）。它是那个场景存在的唯一理由。
21. **不要拿 `LanRemote.App` 验收传输层**：它 `ProjectReference` 了 `LanRemote.Transport`
    却**零调用**。打它的包只能重证 discovery。
22. **不要为了跑通场景去改产品协议**：评审提过「成功场景可以在应用数据里带场景 ID」，
    这是典型的「为测试改被测对象」。用 4 元组关联即可。
23. **不要在验收器里用 UI 线程做测量**：`MainWindow` 的点击处理器是 `async void`，
    而 `ClientRole` 没有 `ConfigureAwait(false)`——续体会被投回 UI 线程，
    日志区滚动/排版时会延迟执行，正好打在靠时间判定的场景上。headless 一律走 `Task.Run`。
24. **不要把「prompt 里怎么描述」当成「代码里怎么实现」**（2026-09-21）：第二轮评审材料把
    `HelloTimeout`（**已声明、未接线**）写成了「五段之一」，评审据此做了正确的加法推演、得出错误的
    「~20 s」结论；服务端实际最坏 = 前缀 5 s + payload 10 s = **15 s**。写「已建成系统」类材料前，
    每一条都要能指到代码/测试证据（该评审另有两处真发现，见 §18.4）。
25. **分段绝对时限不蕴含「总量有界」**（2026-09-21）：`FrameReader.ReadFrameAsync` 两段各自绝对
    （防滑动窗口），但**顺序执行即可加和**（5 s + 10 s = 15 s）；8 个准入槽循环占用依旧成立。
    凡「多段顺序等待」的场景必须显式外层信封 + 专项测试——M3.1 落地 `PreAuthEnvelopeTimeout`（§18.4）。

## 18. M4（Access Key Challenge Auth）· 计划与执行

**状态：完成（2026-09-29）。** 阶段0–5及原21步均完成；§18.5–24为各轮历史执行记录，当时的“尚待/未完成”不覆盖§18.25最终收口。阶段0含ADR-027落地；机器窗口、审批截止及GUI专项修正均保留原证据链。
（M3 已全链闭环：24 步 + 两机验收 PASS + 两机 lab 还原；仓库已推 GitHub。）
本计划按 `LanRemote_Implementation_Package/04_PROTOCOL_AND_SECURITY.md` §9/§14/§15 +
`07_MILESTONES_AND_TASKS.md` M4 编制；阶段 0 的盘点结论已落为 ADR-037/038（`docs/DECISIONS.md`）。

**规格任务（9 项）**：AuthChallenge；canonical transcript builder；HMAC proof；server proof；
timeout；failed auth limiter；local approval dialog；sessionToken；session registry。

**规格测试（8 项）**：deterministic transcript；correct key success；wrong key fail；
modified cert fingerprint fail；modified permission fail；expired challenge fail；
5 failures limiter；serverProof client validation。

**DoD（2 条）**：绝不发送 raw access key；auth success 后才能有 session。

### 18.1 从 M3 继承的硬约束（开工前必修）

- **ADR-028**：transcript 必须绑定 M3 实际出示的 `presentedPin`
  （`ConnectionIdentity.PresentedCertSha256`，不可变）——否则留下「一条连接收 nonce、
  另一条连接中继」的凭据中继缺口（§16 有专条）。
- **`PreAuthenticated_Allows_Nothing_Before_M4` 门禁**：M4 落地时往
  `AllowedOperationsWhilePreAuthenticated` 里加的应当是「开始访问密钥认证」**这一项**，
  而不是「顺手先支持的」能力（§16）。
- **ADR-027**：同 deviceId 不同指纹不得静默覆盖 → `IdentityConflict`，最晚 M4 落地
  （M3 有意推迟，A-4 在 M3 故意没有测试）。
- **auth 帧解析照 `HelloFrame` 模式**：`AllowDuplicateProperties=false`、
  `UnmappedMemberHandling=Disallow`、显式 `MaxDepth`、拒绝尾随数据、非法 UTF-8 不兜底。
- **pre-auth 单帧 4 KiB 上限（ADR-033）**：challenge / response 都在认证前，受此上限约束。
- **禁用**：raw key 绝不上网；key / proof 绝不进日志；不自研密码学（白名单外的一律不做）。

### 18.2 阶段划分（草案，21 步）

**阶段 0 —— 衔接盘点与定案（开工第一件事）—— ✅ 已完成（2026-09-21，见 §18.5）**

1. ✅ 读 `ControlPreAuthSession` / `ControlSessionState` / `TransportHost` 现状，定
   「PreAuthenticated 之后」的衔接：**定案 (b) 显式交接**，落为 **ADR-037**
   （`ControlPreAuthHandoff` 线性所有权 + exactly-once + `ConnectionSecurityContext` 冻结传递；
   M3 的「成功即干净关闭」终态被显式交接取代）。
2. ✅ 定 `AllowedOperationsWhilePreAuthenticated` 的 M4 改造形式与门禁测试的改写：**恰好一项
   `"begin-authentication"`**；旧空集合测试**有意识改写**（精确集合 + exactly-once 行为），
   见 ADR-037 第 5 条。
3. ✅ ADR-027 `IdentityConflict` 定案（`DiscoveryDeviceCache` 冲突字段与丢弃策略）——
   **并已随本阶段落地**（实现 + 8 条新测试 + 1 处既有测试有意识改写；见 §18.5）。
4. ✅ 第二轮外部评审输入：已回收（2026-09-21，见 §18.4）。

**阶段 1 —— transcript + HMAC proof（纯函数核心）** ✅ 完成（2026-09-22，执行记录见 §18.6）

5. ✅ `AuthProtocol` 常量：版本串 `LANREMOTE-AUTH-V1`、字段名、各时限。
6. ✅ `AuthTranscriptBuilder`：固定字段顺序、`\0` 分隔、base64 canonical、uppercase hex；
   输入全部字节级确定（uuid 串 / base64 / hex / 枚举）。
7. ✅ `clientProof = HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)`；
   `serverProof = HMAC-SHA256(accessKeyBytes, UTF8("server\0") || ServerGrantTranscript)`
   ——**本条已按 ADR-038 修订为双档拆分**：计划原文的「serverProof 绑单 transcript」作废，
   现为「绑 ServerGrantTranscript（域分隔 `LANREMOTE-GRANT-V1` + SHA256(client 档) + granted）」。
8. ✅ 测试：deterministic transcript（同输入字节级相同）；correct / wrong key；
   modified cert fingerprint / modified permission（改任一字段必改 proof）；
   另加：黄金向量逐字节（3 向量 + granted 篡改对照）、双档 NUL 分割（8/3 段）、参数校验。

**阶段 2 —— 认证消息帧（JSON 严格解析）** ✅ 完成（2026-09-22，执行记录见 §18.7）

9. ✅ `AuthChallengeFrame`（→client）：`sessionId` / `serverDeviceId` / `serverNonce`(b64 32B) /
   `certSha256`(HEX) / `expiresInMs`——另含 `type` 共 7 字段；拒绝码族 `challenge-*`。
10. ✅ `AuthResponseFrame`（→server）：`clientDeviceId` / `clientName` / `clientNonce` /
    `requestedPermission` / `clientProof`(b64)——`clientName` 硬化（非空 / ≤64 /
    无控制字符 / UTF-16 良构；ADR-040 第 6 条）。
11. ✅ `AuthSuccessFrame` + `ApprovalPendingFrame`（→client）：`grantedPermission` /
    `serverProof` / `sessionToken` / `videoAttachExpiresInMs`——**另加计划外
    `AuthenticationFailedFrame`**（阶段 3/4 公共前置件；ADR-040 第 5 条，记录在案）。
12. ✅ `base64 canonical` 定义与测试——**判定式 = round-trip 逐字符相等**；
    另有 canonical HEX / canonical GUID 同规则、跨帧 wrong-type 分类（ADR-040 第 1/3 条）。

**阶段 3 —— 服务端认证状态机** ✅ 完成（2026-09-22，执行记录见 §18.8）

13. ✅ challenge 生成（**构造期定稿**：`sessionId` / nonce32 / `certSha256`←冻结上下文 /
    `expiresInMs=15000`）与超时（**绝对** deadline = 机器窗口 10 s，覆盖写/读/校验；
    压线后置校验）。
14. ✅ 服务端验证链：限流前置 / 机器窗口 / 帧级严格解析 / 密钥加载（**解析通过后才触碰**）/
    重算 + `FixedTimeEquals`（唯一计限流失败 + 300–800 ms 随机延时）/ 失败一律 generic
    `authentication_failed`。**「session+device 对齐」不做独立字段比对**——proof 重算把
    session / device / 两端 nonce / 证书指纹全部隐式绑死（跨会话重放必然 MAC 失败）。
15. ✅ failed auth limiter（按 remote IP）：10 分钟窗口 / 5 失败 → 拒 60 s /
    每次失败 300–800 ms 随机延时 / 成功清计数 / 不打日志；封禁到点放行、再失败即再封。
16. ✅ local approval（`ILocalApprovalGate` 5 值显式终态 + fail closed；v1 = 每个新控制连接
    都要批，ADR-038）+ `approval_pending` 流程（三路竞速 + 单读者复用）。
17. ✅ `sessionToken`（32 随机字节）+ `SessionRegistry`（**success 字节写出成功**才注册
    ——ADR-041 第 1 条；DoD 的可执行形式）。

**阶段 4 —— 客户端侧：实现、自动化、验收器展示及实屏确认完成（§18.10/§18.11/§18.25）**

18. [完成] 客户端认证流程：hello → challenge → clientNonce → transcript（**绑 presentedPin**）→
    response → success → 验证 serverProof；高层入口独占中间连接，成功才返回已认证会话。
19. [完成] serverProof 验证失败 → 立即断开 + 固定文案「远端身份验证失败，可能是错误密码或伪造设备广播」+ 不发送输入。b42a1553真实错误proof日志与2026-09-29用户实屏确认配齐，见§18.23/§18.25。
20. [完成] 客户端 e2e（真 TLS 双端）：correct key success；wrong key `authentication_failed`；
    serverProof 篡改必拒；独立 UTF-8/HMAC 对端及权限/身份/时限/秘密副本测试已覆盖。

**阶段 5 —— 收口**

21. [完成] 全量 `build` + `test`、真实回环TLS接线、8项运行期变异、既定两机/GUI人工清单及HANDOFF记账完成。最终代码基线双配置各1433 PASS，GUI双击即窗口；原范围联网/审批/密钥/错误提示/关窗及活动停止均已配齐（§18.15–25），不增加全DPI或全产品验收门槛。

### 18.3 未决点（阶段 0 / 用户对齐）—— 均已关闭

- 衔接层位置：**已定 (b)**——显式交接（`ControlPreAuthSession → ControlAuthSession`），
  评审与本机基线一致；增强件（线性所有权对象、exactly-once、测试改写）见 §18.4。
  **已落为 ADR-037（2026-09-21）。**
- `local approval dialog` 的 M4 边界：**已拍板 (b)（2026-09-21）**——`ILocalApprovalGate` 库真接口
  + 单测替身 + 验收器最小审批面（评审论证推翻了本机此前 (a) 倾向；本机采纳评审论证）。
  产品 WPF UI 仍不动。
- ADR-027 冲突字段与丢弃策略：**已定案并落地（2026-09-21）**——ADR-027 修订段 + §18.5 C 节。
- 第二轮外部评审的时机：**已回收**（2026-09-21，先行完成，见 §18.4）。

### 18.4 第二轮外部评审结论 → 计划修订（2026-09-21 回收）

分流全文：`docs/M3_IMPLEMENTATION_REVIEW_TRIAGE.md`（含 3+2 处「评审前提 ≠ 代码事实」的逐条核对）。
计划层面的净修订如下。

**A. 先做：M3.1 加固（M4 之前；少量代码 + 测试补强）—— ✅ 已完成（2026-09-21）**

- `PreAuthEnvelopeTimeout`（初值 **8s**，provisional）：pre-auth **外层信封**——自会话进入起算、
  永不重置，覆盖前缀 + payload + 解析 + 收尾；修复「分段绝对 ≠ 总量有界（5+10=15 s 可加和）」。
  测试=吃满前缀再拖 payload，必须在信封到点被切（缩放值）。
- `HelloTimeout` 语义收拾：它**从未被服务端接线**（唯一消费=验收器客户端写超时 + 日志行）——
  文档改为写预算，不再暗示独立顺序段（§17 教训 #24）。
- 测试补强：跨 listener 共享限额（B15）；真实 TLS 粘包（B16）；字节边界取消矩阵（B19）；
  准入释放矩阵补路径（B20）；`StopAllAsync` 未完成计数 + false 路径测试（B18）。
- **不重开** M3 两机验收（信封用本机真实 TLS 集成测试证明）；如再跑验收：物料需重打（src 有改动）。

**A. 执行记录（2026-09-21 收尾，单机全量验证）**

- 全部按上述范围落地（9 步批次），实现 == 计划，无范围外改动。
- 变异验证（贯穿纪律；恢复后均以 `git diff` 确认为空）：真实 TLS 帧边界 ×2、跨 listener 限额
  与名额归还 ×2、停机未完成计数 ×1、字节边界矩阵 ×2——全部「禁用实现 → 精确变红 → 恢复 → 全绿」。
  例：字节边界矩阵的单读变异 13 红 / 14 绿；停机计数变异精确点红「未完成计数」与「停机预算」
  两条用例（其余 17 条不受影响）。
- B19 一处设计纠错（值得记住）：**读取器消费掉的字节无法退回**——「半前缀超时后再读」必然错位
  （读侧把后 2 个前缀字节当成新前缀头 → 前缀值巨大 → 超限拒绝，`FrameProtocolException`）。
  恢复性场景必须用 **0 字节失败**；对应用例定为「0 字节超时 → 后续读完整帧成功」。
- 验收器同步：`AcceptanceProfile`（信封 8 s + 既有判据核对：所有场景的期望收尾时刻早于信封；
  slow-dribble 判据 `sent<4` 不受影响）；`HostRole`（停机改用 `TransportHostStopReport`，SUMMARY
  增 `unfinishedConnections` / `acceptLoopsFinished`）；`AcceptanceRun`（`deadline.hello` 行语义
  修正——标注「客户端写预算；服务端不消费」；新增 `deadline.preAuthEnvelope` 行）。
- 全量验证：Debug + Release `dotnet build` **0 警告 0 错误**；`dotnet test` **601 PASS / 0 FAIL**
  （Protocol 215 + Transport 192 + Core 125 + Security 66 + Integration 3）。
- 未重开 M3 两机验收（同计划）；如之后重跑两机：验收物料必须重打。

**B. M4 计划修订（阶段 0 落实为 ADR-027 修订）**

- **transcript 拆两个**：`ClientAuthTranscript`（绑 requestedPermission）与 `ServerGrantTranscript`
  （域分隔 + H(客户端 transcript) + grantedPermission）；`serverProof` 绑后者——修复「双 proof 同 transcript
  无法绑定尚未决定的 granted」（评审最重要的设计发现）。规格内部冲突「`server|` vs `server\0`」以 04 为准。
- 衔接：**(b) 显式交接**；线性所有权对象（live stream + 冻结安全上下文 + connectionId + 生命周期），
  exactly-once；门禁改为「`PreAuthenticated` 只允许一次 `BeginAuthentication` 转移」；
  旧空集合测试**有意识改写**而非删除。
- dispatch 显式门禁 `state == Authenticated && granted >= required`（空集合仅 defense-in-depth）。
- 限流：只计「到达密码学校验且失败」；聚合遥测（A8）；审批拒绝/超时**不**计（D2/D3 裁定）。
- 审批：**v1 每个新控制连接都要批**（不做运行期记住）；`PendingApproval` 独立配额（初值全局 3 / 源 1）；
  显示最小集 + 短关联码；请求不可变、原子终态、断连不发 token；UI 不可用 fail closed。
- 时限初值（provisional）：机器认证 **10s** / 人类审批 **60s**（获批面受理起算）。
- `ConnectionSecurityContext` 冻结传递（含本连接实际证书指纹）；`certSha256` 从该上下文派生。

**C. 数值实验（评审 D1；全部 [NEEDS LOCAL EXPERIMENT]，先不动数值）**

- ① 冷启动 + 忙 CPU（TLS 5s）；② 受控首 SYN 丢失（connect 3↔5s）；③ Wi-Fi 抖动（如适用）；
  ④ 8 并发握手资源（顺带核 global=8）。完成后一次定案（五段 + 信封）并进 ADR。
- **丢包实验涉及环境改动——走用户通道，不静默动网。**

**D. 待用户拍板（M4 开工前）—— ✅ 已拍板（2026-09-21）：(b)**

- 审批机制边界：评审建议 (b)（验收器加最小审批面）vs 本机此前 (a)（纯抽象 + 替身）——
  用户已确认 **(b)**：`ILocalApprovalGate` 库真接口 + 单测替身 + 验收器最小审批面。
  M4 阶段 0 的审批设计按 (b) 落地。

---

### 18.5 阶段 0 执行记录（2026-09-21）—— 衔接盘点 / 定案 / ADR-027 落地

**A. 源码盘点结论（读代码得出的事实，非推测）**

- `ControlPreAuthSession.RunAsync` 成功路径 = `State=PreAuthenticated` → **立即 `ShutdownAsync`** →
  返回 `ControlPreAuthResult(true, PreAuthenticated, null)`；结果类型**不携带**任何续行对象
  （无流 / 无身份 / 无 connectionId）。M3 步骤 21 有意没做 `AwaitingAuthentication` 占位 → 衔接必须显式。
- `AcceptedConnection` 只有 `(LocalAddress, RemoteAddress, RemotePort, Stream, NegotiatedProtocol)`——
  **无 connectionId、无服务端证书指纹** → M4 需要 `ConnectionSecurityContext` 补这两样（ADR-037 第 3 条）。
- 唯一产品级接线点 = 验收器 `HostRole`（`new ControlPreAuthSession()`）；`LanRemote.App` 未接线
  （「别拿 `LanRemote.App` 验传输层」依旧成立）。
- 门禁测试 = `PreAuthenticated_Allows_Nothing_Before_M4`（`Assert.Empty`）——改写方案见 ADR-037 第 5 条。
- 流所有权：`TransportHost.HandleAsync` 的 `finally` 释放（Host 是最终拥有者）——M4 交接不改变这一点。

**B. 定案产出（`docs/DECISIONS.md`）**

- **ADR-027 修订**：M4 阶段 0 实现定案（冲突字段 `Entry { Device, Conflicts }` / 每目 8 条上限 /
  `IsIdentityConflicted` 惰性清除 / 主条目整体过期的显式边界）。
- **新增 ADR-037**：M4 衔接层（显式交接 + 线性所有权 `ControlPreAuthHandoff` + exactly-once +
  `ConnectionSecurityContext` 冻结传递 + `ControlSessionState` 扩展 + allowed-ops 恰好一项 +
  流所有权链 + 认证成功后的连接归宿 + 4 KiB 帧上限沿用 + 旧测试改写清单）。
- **新增 ADR-038**：M4 认证协议定案（**双 transcript 拆分**含精确字节布局 / `server\0` 域前缀 /
  限流只计「到达密码学校验且失败」 / 审批 v1「每个新连接都要批」+ PendingApproval 配额 3/1 /
  时限初值 10s + 60s（provisional） / DoD 两个可执行形式）。

**C. ADR-027 落地件（本阶段唯一的代码改动）**

- `DiscoveryDeviceCache`：`Dictionary<Guid, DiscoveredDevice>` → `Dictionary<Guid, Entry>`；
  `Upsert` 分流（同指纹照旧 last-write-wins；异指纹只记冲突、不覆盖主条目）；
  新增 `IsIdentityConflicted` / `ConflictingFingerprintCount`；冲突与条目过期清理。
- 测试：`DiscoveryDeviceCacheTests` **+8**（ADR 验证 ①②③ + 大小写不敏感 + 冲突被刷新则持续 +
  主条目整体过期边界 + 上限有界 + unknown）；**既有测试 1 处有意识改写**：
  `TlsClientConnectorTests.Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`——
  M3 版本假设「缓存会被 last-write-wins 换指纹」，ADR-027 落地后该假设失效；改写后同时断言
  「投毒不落地（主条目保持 pinA + 冲突被标记）」与「冻结快照不受影响」+ 伪造目标必失败
  （验证面比 M3 版本更宽）。**测试名保留**（3 处文档引用：TRIAGE / 本节 / §16 表）。
- 变异验证（3 次；恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净）：
  - α「静默覆盖」（恢复旧行为）：Protocol **4 红**（4 条冲突用例精确命中）；
  - γ 重放 α（验证改写后的 `Frozen_Pin`）：该测试亦红——**共 5 红**，两条防线都被测到；
  - β「不清理过期冲突」：**精确 1 红**（`Conflict_Clears_AfterConflictingObservation_Expires`）。
- 全量验证：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **609 PASS / 0 FAIL**
  （Protocol 223 + Transport 192 + Core 125 + Security 66 + Integration 3）。

**D. 遗留 / 下一站**

- 下一站 = 阶段 1（transcript + HMAC proof 纯函数核心）；落地时同步
  `docs/PROTOCOL_AND_SECURITY.md` 工作副本与 ADR-038 的差异（**以 ADR-038 为准**）。
- 两机验收重跑仍不急：物料需重打（本轮已含 Discovery 改动），归入 M4 收口批次。

### 18.6 阶段 1 执行记录（2026-09-22）—— 双档 transcript + HMAC proof 纯函数核心

**状态：完成**（提交 `35506b5`，6 files，+1113/−11；步骤 5–8 全落地）。
**同日重定位 `e7687ec`**（ADR-039）：阶段 2 开工盘点发现 TFM 硬约束（Transport net10.0 无法引用
Security net10.0-windows7.0——NU1201 本机实测），认证核心由 Security 搬至 Transport，见下。
§18.5 D 节遗留：「工作副本同步 ADR-038」**已完成**；「两机验收重跑」仍挂账（物料需重打）。

**产出与落点**（路径为重定位后；重定位为纯搬移，协议字节与测试语义零变化）

- `src/LanRemote.Transport/Auth/AuthProtocol.cs` —— 协议词汇表单一事实源
  （原 `Security/Auth`，ADR-039 重定位）：
  两个域串（`LANREMOTE-AUTH-V1` / `LANREMOTE-GRANT-V1`）、`server\0` 前缀、5 个帧 type、
  16 个 JSON 字段名、4 个尺寸（nonce/proof/token/cert 均 32B）、两个时限初值
  （认证 10s / 审批 60s，provisional，待数值实验回写）、权限词（`view`/`control`）
  + `EncodePermission`（未定义枚举值抛异常——防将来扩展枚举时静默误编码）。
- `src/LanRemote.Transport/Auth/AuthTranscriptBuilder.cs` —— 纯函数核心
  （原 `Security/Auth`，ADR-039 重定位）：
  `BuildClientTranscript` / `BuildGrantTranscript` / `ComputeClientProof` / `ComputeServerProof`。
  输入全部强类型（`Guid` / `ReadOnlySpan<byte>` / 枚举）、**不接收 string**（规范形式只由本类
  输出，从类型上消灭「对端编码差异（hex 大小写 / 非规范 base64 / uuid 格式）进 transcript」
  整类问题）；固定长度字段（nonce×2 / cert / key）不符即 `ArgumentException`（fail fast）。
- `scripts/reference/gen-auth-golden-vectors.py`（入库）—— 独立 Python 参考实现（只用标准库
  hmac/hashlib/base64/uuid）；输出 3 个黄金向量（control/control、view/view、
  requested=view+granted=control）+ granted 篡改对照向量 + 可直接粘贴的 C# 常量块
  （NUL 用 `\u0000` 转义——C# 的 `\0` 后跟数字会被解析为八进制转义，是实测踩过的坑）。
- 测试 +34：`AuthProtocolTests` 8 条字面量锁定；
  `AuthTranscriptBuilderTests` 26 条 case（黄金向量逐字节/hex、client 档 8 段 / grant 档 3 段
  NUL 分割、域分隔、字段敏感性、granted 篡改必致 serverProof 验证失败、前缀参与 MAC、参数校验）。
  落点随重定位迁移（Security.Tests 66→100→66、Transport.Tests 192→226）。
- `docs/PROTOCOL_AND_SECURITY.md` 工作副本 §9 同步 —— 头部「本地修订记录」+【本地修订 1】
  （双档 transcript + serverProof 绑 grant 档；原文保留对照）；该文件自此进入「随里程碑修订」模式。

**变异验证（4 次）** —— 恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净：

- M1「client 档追加尾随 `\0`」→ **8 红**（3 向量链 + 8 段分割 + granted 篡改对照）；
- M2「cert 改小写 hex」→ **8 红**（同组；uppercase 合同受黄金向量保护）；
- M3「serverProof 去 `server\0` 前缀」→ 首轮 4 红；**由此发现
  `ServerProof_DependsOnServerPrefix` 只断言常量关系、不断言实现输出**（变异下不红）——
  立即强化为「实现输出 ≠ 无前缀 HMAC」，**重放 M3 确认 5 红**；
- M4「移除 serverNonce 长度校验」→ **精确 3 红**（theory 全 case，零误伤）。

**全量验证**：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **643 PASS / 0 FAIL**
（Protocol 223 + Transport 192 + Core 125 + Security 100 + Integration 3；重定位后分布 =
Transport **226** / Security **66**，总数不变）。

**下一站 = 阶段 2（认证消息帧 JSON 严格解析）**：4 类帧 + canonical base64 校验/解析；
照 `HelloFrame` 严格模式（重复字段 / 未知字段 / 大小写 / 深度全写死 + 逐项测试）。

### 18.7 阶段 2 执行记录（2026-09-22）—— 认证消息帧（JSON 严格解析）

**状态：完成**（提交 `21a8829`，18 files，+3031；§18.2 步骤 9–12 全落地，另加 1 个计划外帧）。

**产出与落点**

- **帧类 ×5**（`src/LanRemote.Transport/Auth/`；API 形态 = `TryParse(ReadOnlySpan<byte> utf8,
  out XFrame? frame, out string? rejection)` + `Serialize()`；纯标志帧 `ApprovalPendingFrame` /
  `AuthenticationFailedFrame` 用 static class + `TryParse(ReadOnlySpan<byte>, out string?)`）：
  - `AuthChallengeFrame`（步骤 9）：`type`/`protocol`/`sessionId`/`serverDeviceId`/
    `serverNonce`/`certSha256`/`expiresInMs`；拒绝码族 `challenge-*`（10 个短码）。
  - `AuthResponseFrame`（步骤 10）：`clientDeviceId`/`clientName`/`clientNonce`/
    `requestedPermission`/`clientProof` + `type`；`clientName` 硬化（非空 / ≤64 / 无控制字符 /
    UTF-16 良构——名字最终出现在被控端本机审批面）。
  - `AuthSuccessFrame`（步骤 11）：`grantedPermission`/`serverProof`/`sessionToken`/
    `videoAttachExpiresInMs` + `type`；`grantedPermission` 必须 `view`/`control`。
  - `ApprovalPendingFrame`（步骤 11）：唯一字段 `type`；序列化恰为
    `{"type":"approval_pending"}`（字节稳定测试锁定）。
  - `AuthenticationFailedFrame`（**计划外补充，已记录在案**）：语义为空、永不携带原因码——
    认证失败唯一对外形式（阶段 3/4 公共前置件，避免届时手写第二套 JSON 解析面）。
- **canonical 编码 ×3**（步骤 12）：`CanonicalBase64` / `CanonicalHex` / `CanonicalGuid`——
  判定式统一 =「宽松 decode → 重新 encode → Ordinal 比较」（round-trip 逐字符相等）。
- **`AuthJson` 增类型提示预读**（可选参数 `expectedType`/`wrongTypeCode` + `TryReadTypeHint`：
  扫根对象第一层 `type` 字符串，嵌套用 `TrySkip` 跳过）——跨帧载荷（结构合法、类型是别的帧）
  报 wrong-type 而非 malformed；主解析仍是唯一权威，提示不影响接受与否。
- **测试 +234**：3 个 canonical 测试文件（往返扫掠 1..96 覆盖 padding 余数 / 大写字面量 /
  `Accepts_Every_ToHexString_Output…` / D 格式收窄）+ 5 个帧测试文件（结构 = 基线字面量 +
  `Build(...)` 变体构造器 + `Patch(from,to)` + `Rejects(json, expected)` 断言器；逐项覆盖：
  重复/未知字段、大小写、注释、尾逗号、尾随数据、非法 UTF-8/BOM、null=missing、跨帧交叉拒绝
  （真实序列化字节互喂全 wrong-type）、构造器 fail-fast、输入缓冲复制、拒绝短码不含输入）。

**实测事实（本阶段新增，全部实锤）**

- `Utf8JsonReader.GetString()` 对非法 UTF-8 抛 `InvalidOperationException`（内层
  `DecoderFallbackException`），**不是** `JsonException`（`JsonSerializer.Deserialize` 才会包成
  JsonException）→ `TryReadTypeHint` 必须两类都捕。
- `System.Text.Json` 解码孤立代理转义 `\uD800` 时**直接抛 `JsonException`** → 归 malformed，
  到不了 bad-name（测试预期按实测修正并留注释；`IsAcceptableClientName` 的代理判定保留，
  保护构造器路径）。
- `Guid.TryParseExact("D")` 除容忍大写外**还容忍前后空白**——round-trip 是唯一收窄者
  （M2 变异意外收获：canonical GUID 的空白用例跟着红）。
- `"AA++"` / `"AA//"` 是**合法** canonical base64（`+`/`/` 属标准字母表）——写测试时先入
  为主误判为非法、被实测纠正（拒绝组换成 `AA--`/`AA__`）。

**设计微决策（记录在案；合同级内容已 ADR-040 化）**

- 帧内判定顺序：type 缺失 → type 不等（wrong-type）→ 其余字段存在性（missing）→ 逐字段判值；
  null 值按 missing 报。
- `expiresInMs` / `videoAttachExpiresInMs` 只做「正值」判定——上界不在帧层发明，
  由消费方绝对 deadline 兜底。
- 构造器 fail-fast：长度 / 非正数 / 未定义枚举（`_ = AuthProtocol.EncodePermission(...)`）
  立即抛；输入数组 `ToArray()` 复制防外部突变。
- `AuthProtocol.TryDecodePermission` 补齐解析方向（`EncodePermission` 的逆；大小写精确）。

**变异验证（4 次）** —— 恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净：

- M1「`AllowDuplicateProperties=true`」→ **5 红**精确（3 条 `Rejects_Duplicate_Fields` +
  2 条无载荷帧重复字段）；
- M2「`CanonicalGuid` round-trip 失效」→ **6 红**（3 条 bad-id + 3 条 canonical 收窄用例）；
- M3「challenge `serverNonce` 长度判定摘除」→ **首轮 0 红** → 当场抓到一条**假测试**：
  「31 字节」手抄字面量实为 45 字符（excess padding，根本不是合法 base64），测试从未触到
  长度判定、被别的拒绝路径救活；修复 = 测试加 `B64(int)` helper（BCL `Convert.ToBase64String`
  构造 16/31/33 字节邻界值，不再手抄长串），**重放 M3 → 精确 1 红**
  （`Rejects_Bad_Server_Nonce`）；同款坏字面量在 response / success 两个测试文件同步修掉；
- M4「success `serverProof` 长度判定摘除」→ **精确 1 红**（`Rejects_Bad_Server_Proof`——
  验证修复后的 success 邻界用例同为真测试）。

**全量验证**：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **877 PASS / 0 FAIL**
（Protocol 223 + Transport 460 + Core 125 + Security 66 + Integration 3；
Transport 226→460 = +234）。

**下一站 = 阶段 3（服务端认证状态机，步骤 13–17）**：challenge 生成（绝对 deadline）/
验证链（重算 + `FixedTimeEquals`，失败一律 generic `authentication_failed`）/
failed auth limiter（按 remote IP）/ local approval + `approval_pending`（v1 = 每个新连接
都批，ADR-038）/ sessionToken + `SessionRegistry`。衔接层（ADR-037 的
`ControlPreAuthHandoff`）随之落地，门禁测试改写与实现同批（写了才有 → 能测）。

### 18.8 阶段 3 执行记录（2026-09-22）—— 服务端认证状态机（步骤 13–17 + 衔接层落地）

**状态：完成**（提交 `760e950`，14 files，+2780/−89；§18.2 步骤 13–17 全落地 + ADR-037 衔接层
同批落地；测试 +25，Transport 460→485；全量 **902 PASS / 0 FAIL**。）

**产出与落点**

- **衔接层（ADR-037 的可执行形式，与状态机同批）**：
  - `ConnectionSecurityContext`（77 行）：本连接安全事实冻结快照——`ConnectionId` / 地址端口 /
    协商 TLS 版本 / **本机证书 DER SHA-256（32 字节）**；`TransportHost` 构造期算一次、
    全生命周期冻结；challenge `certSha256` 的唯一来源（「声称 = 实际出示」）。
  - `AcceptedConnection` 由 5 参记录收编为 `(ConnectionSecurityContext Security, SslStream Stream)`
    + 4 个转发属性（既有消费点零破坏）。
  - `ControlPreAuthHandoff`（59 行）：线性所有权交接对象；`BeginAuthentication` **exactly-once**
    （第二次抛 `InvalidOperationException`，消息含「只允许」）。
  - `ControlPreAuthSession`：成功路径**不再** `ShutdownAsync`（M3 终态被有意识改写，注释保留
    理由与原文）；`AllowedOperationsWhilePreAuthenticated` 空集合 → **恰好一项**
    `begin-authentication`（门禁测试 = 精确集合相等）；`ControlPreAuthResult` 增 `Handoff` 字段。
  - `ControlSessionState` 扩展 `Authenticating` / `Authenticated`（一个枚举贯穿全链）。
- **状态机（步骤 13–17）**：
  - `ControlAuthContext` + `ControlAuthOptions`（85 行）：跨连接共享守卫（密钥存储 / 限流 /
    待批配额 / 审批面 / 登记表 / 时钟）+ **值旋钮**（全部时限可整体缩放 = 测试能测超时路径的前提）。
  - `ControlAuthSession`（664 行）：`RunAsync` 八段主干 = ①限流前置（被罚 IP 连 challenge
    都不发）→ ②机器窗口（10 s **绝对** deadline，覆盖 challenge 写 + response 读 + 校验判定）→
    ③帧级严格解析 → ④密钥加载（**解析通过后才触碰**）→ ⑤重算 + `FixedTimeEquals`（唯一计限流
    失败 + 300–800 ms 随机延时）→ ⑥审批（quota → `approval_pending` → 三路竞速）→
    ⑦serverProof + token + `auth_success` → ⑧登记 + 保持。challenge 构造期定稿
    （sessionId / nonce 此后不变）。
  - `FailedAuthLimiter`（166 行）：10 分钟滑窗 / 5 失败 → 60 s 封禁；封禁到点**放行**、
    再失败即再封（净效果：持续攻击被压到「每 60 s 一次尝试」直到旧记录滑出 10 分钟窗）；
    成功清全部（含封禁）；条目懒清理；`TimeProvider` 可注入；零日志。
  - `LocalApprovalGate`（119 行）：`ILocalApprovalGate` + `LocalApprovalOutcome` **5 值显式终态**
    （无 bool、无 handler ≠ 同意、UI 不可用必须 Unavailable）；不可变请求快照（自称字段标注）+
    短关联码 = `SHA256("LANREMOTE-APPROVAL-CODE-V1"\0‖sessionId‖clientNonce)` 前 3 字节大写 hex。
  - `SessionRegistry`（185 行）：`Register` 为 **internal 唯一入口**（认证状态机在
    `Authenticated` 调用）；token 防御性拷贝；`SessionRegistration.Dispose` → 注销 + token 清零；
    公开面只有计数 / Snapshot（无 token、无注册入口——反射测试钉死）。
- **修改 ×6**：`AcceptedConnection`（+37/−）、`AuthProtocol`（+31：8 常量）、
  `ControlPreAuthSession`（+76/−：交接改写）、`TransportHost`（+16：指纹冻结 + 上下文构造）、
  `ControlPreAuthSessionTests`（+108/−：适配 2 参构造 + 门禁改写）、`HostRole`（±5：
  `localShutdownSent` UNOBSERVED 行更新——M4 起成功路径不再关闭连接）。
- **测试 +25**（`ControlAuthSessionTests.cs`，1241 行；真实回环 TLS + 真实 `TransportHost` 全链；
  21 方法 = 20 Fact + 1 Theory×5）：
  - 交接 exactly-once ×2：`BeginAuthentication_Is_Exactly_Once_Per_Handoff`（detached 流）+
    `Auth_Session_Cannot_Be_Run_Twice`（第二次必抛「只允许跑一次」）。
  - 成功全链 ×1：帧序列**恰为** `[auth_challenge, approval_pending, auth_success]`；客户端
    重算 proof / 验证 serverProof（更正：该旧 probe 复用了产品构造器，不是独立密码学 oracle；阶段4另补独立 UTF-8/HMAC 对端）；登记摘要逐字段；token 与
    下发逐字节相同；保持探针；断开后注销清零。
  - 失败面 ×6：错钥（计数 = 1）/ 篡改 permission / 篡改指纹（签名页 ≠ 报文页构造）/
    帧违规（零长度前缀 → `auth-frame:length-zero`，不计）/ 断连 EOF（不计）/ 密钥存储失败
    fail closed。
  - 时限/限流 ×2：机器窗口 700 ms 截断拖流（可证伪耗时断言）/ 被限流源**收不到 challenge**。
  - 审批面 ×8 方法：终态 Theory ×5（denied / cancelled / unavailable / timedout /
    抛异常→Unavailable，全 fail closed）/ 错请求 ID / 越权授予拒 / 降级 view-only 允许 /
    窗口截断 + 迟到决定作废 / 断连作废迟到决定 / 配额截断第二个待批（双连接）。
  - 数值/DoD ×3：失败延时边界（200–400 ms 上下界）/ 反射（公开面无注册无 token）/
    帧键集白名单（`AssertJsonKeySet` 逐帧精确相等）。

**实测事实（本阶段新增，全部实锤）**

- **测试夹具竞速（本阶段最贵的一课）**：harness 在服务端出结局后立即拆线（cancel + dispose），
  与客户端脚本「读最后一帧」构成竞速——快速失败路径（错钥 / 被限流 / 即时拒绝）下偶发
  「收不到 generic 失败帧 / approval_pending」；修复 = 拆线前先
  `await Task.WhenAny(clientTask, Task.Delay(TimeSpan.FromSeconds(3)))` 给客户端自然收场机会
  （服务端收线后正文先于 FIN 到达，读类脚本毫秒级自结束；静止类脚本由后续 cancel 兜底）。
  **教训：测试夹具的「清理」也是被测系统的一部分**——拆线快 ≠ 对。
- 「迟到决定」测试的延时**不能绑 stall 令牌**（harness 取消它会把这笔「迟到」吞掉，测不到
  目标路径）——改用不绑令牌的 `Task.Delay(150)`。
- `Assert.False(probe.Frames.Contains(...))` 触发 xUnit2017 → 改 `Assert.DoesNotContain`。
- 机器窗口「压线后置校验」（读返回后立即查 `IsCancellationRequested`）在代码里只有两行——
  没有它，「窗口 + 解析耗时」会漂移成实际 deadline。

**设计微决策（记录在案；合同级内容已 ADR-041 化）**

- 失败帧是 best-effort（对端可能早走了，发不出去不改变「已拒绝」）；但**停机取消照常上抛**。
- `ControlAuthResult` 成败都带 `SessionId`（日志关联）；15 个 `auth-*` 前缀失败短码只进本地。
- `RequireLocalApproval=false` 时跳过审批阶段（发 success 前无 `approval_pending`）——测试用。

**变异验证（4 组；恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净）**

- M1「proof 校验恒成功」（`false && !FixedTimeEquals(...)`）→ **4 红精确**：错钥 / 篡改权限 /
  篡改指纹 / 失败延时边界（2026-09-22 提交后原样重放核实）；
- M2「删 `RecordFailure`」→ **3 红**：错钥 + 两条篡改的计数断言；
- M3「`IsGrantable` 恒真」→ **1 红**：`Over_Grant_In_Decision_Is_Rejected`；
- M4「双重拆除断连防线」（`winner == clientActivity` 分支与 `clientActivity.IsCompleted`
  后置校验同时失效）→ **1 红**：`Approval_Disconnect_Discards_A_Later_Decision`——
  顺带证明纵深防御形态：两道防线各自在场时都测不出，**全拆才红**。

**全量验证**：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **902 PASS / 0 FAIL**
（Protocol 223 + Transport 485 + Core 125 + Security 66 + Integration 3；Transport 460→485 = +25）。

**下一站 = 阶段 4（客户端侧，步骤 18–20）**：客户端认证流程（hello → challenge → clientNonce →
transcript **绑 presentedPin** → response → success → **验证 serverProof**）；失败处理
（serverProof 验证失败 → 立即断开 + UI 文案「远端身份验证失败，可能是错误密码或伪造设备广播」+
不发送输入）；客户端 e2e（真 TLS 双端：correct key success / wrong key generic
`authentication_failed` / serverProof 篡改必拒）。落点估计：`TlsClientConnector` 侧接线 +
`AuthTranscriptBuilder` 客户端语义复用（**独立重算**，勿共享服务端算式）。

### 18.9 阶段 4 开工复核：时限合同反例与外部评审停点（2026-09-22，历史）

> 本节是修复前历史快照；评审已回收、用户已拍板、服务端修复和客户端实现已完成，当前进度以 §18.10 为准。下述「未修复/待转发」不再是当前要求。

**当时结论：阶段 3 历史测试保持绿色，但两条缺失覆盖的时限合同已被本机真实 TLS 反例证伪；产品修复未实施，阶段 4客户端尚未编码。** 按用户常驻指令的外部模型通道暂停，待用户转发材料并贴回评审。此处不是阶段完成记账，不回填阶段4为完成。

**代码事实**（基线 `760e950`）：

- `ControlAuthSession.cs:178–224` 的 machine CTS 只包住challenge写、response读和读后取消检查；严格解析、`LoadOrCreateAsync(cancellationToken)`及HMAC比较在外，无完成时限后置判断。ADR-041 §3/此前“覆盖校验全程”的解释不成立。
- `ControlAuthSession.cs:397–398` 先调用gate后创建审批Delay；同步前缀耗时未计入实际Delay。决定分支复核断连和权限，但接受批准前没有截止时间检查。
- “过期不接受”与“到点强制停止同步DPAPI/gate执行”是两个不同合同，不能混用；完整修复待评审，不通过改大provisional数值掩盖。

**实测方法与结果**：临时给 `ControlAuthSessionTests.cs` 加两条诊断用例，走真实回环TLS、真实TransportHost及完整pre-auth→auth状态机；仅store/gate注入耗时。没有改网络或访问实际用户秘密文件。

| 诊断 | 窗口 | 首轮 | 重放 | 两轮结果 |
| --- | --- | --- | --- | --- |
| `Diagnostic_Machine_Window_Must_Include_Key_Load_And_Proof` | 700ms | keyLoad 2111ms | 2113ms | Completed=True；serverProofValid=True；连接保持时registry=1；进入审批 |
| `Diagnostic_Approval_Window_Must_Include_Synchronous_Gate_Call` | 600ms | gate同步1803ms | 1807ms | Completed=True；serverProofValid=True；连接保持时registry=1 |

两轮各 **2 FAIL**，均精确失败于“应拒绝，但Completed=True”；前置条件和客户端无异常断言已通过。帧序列均为challenge→pending→success。诊断验证的是时限，不是独立密码学oracle；没有断言生产DPAPI自然发生这些延迟，也没有运行真实WPF审批面。诊断patch第一条要求慢store完成，仅用于证明当前反例；永久回归需兼容修复后的提前合作取消。

**恢复与验证**：

- 反例已保存 `outputs/m4-deadline-review/deadline-counterexamples.patch`，临时测试从源码删除恢复；`git diff --exit-code -- src tests tools`通过；`git apply --check`确认补丁可重放。
- 恢复后重新Debug build：**0警告/0错误**；全量原有 **902 PASS/0 FAIL/0 SKIP**，分项223+485+125+66+3。未跑Release、两机或数值实验。
- **Last code commit：`760e950`（未改变）。Working tree at validation：src/tests/tools与该代码基线一致；诊断patch及证据独立保留，验证后仅更新文档/记忆/评审材料。** 原有902全绿只表示恢复成功，不能作为两条缺陷已修复的证据。
- 本轮未提交/推送；评审材料和当前补记保留在工作区，待后续收口。不要把未提交文档误认为丢失的代码实现。

**交付/下一步（当时记录）**：用户转发 `outputs/m4-deadline-review/M4_AUTH_DEADLINE_REVIEW_PROMPT.md`，可附同目录 `M4_AUTH_DEADLINE_EVIDENCE.zip`（相关源码、原始日志、patch、哈希清单）。回收后逐条核对前提；先修服务端截止/取消/迟到结果所有权并补永久回归，再实现客户端presentedPin绑定、serverProof门禁和真实TLS e2e，最后阶段5验收器。仍遵守不改产品UI、双击即GUI、不静默改网。

### 18.10 服务端修复与阶段 4 Transport 收口（2026-09-22）

**状态**：代码提交 `bc02a0c`；步骤 18/20 完成，步骤 19 的库层失败处理完成、验收器展示待阶段 5。不是完整 M4 或两机 DoD 通过。外部评审已回收，用户明确选择「按状态机接受时刻」；现行决定 ADR-042/043，取代 ADR-041 的旧时限解释。

**服务端修复**

- `AuthenticationDeadline` 使用单调时间，`elapsed >= budget` 拒绝；timer/token 负责合作式唤醒，timer 未派发不延长接受窗口；统一隔离 timer、父取消和显式取消中的回调异常。
- `AuthenticationSecretLoader` 按认证 context 共用，最多一项未终结的实际 store 工作；取消等待后 late owner 先清零迟到 key 再归还准入，观察 fault/cancel。不每连接另起 Task.Run，不宣称硬中断同步 DPAPI。
- machine 自 challenge 写前覆盖至 MAC 后最终检查；最后检查之前不操作 limiter、不进入审批。及时错误 proof 才计失败，抖动改用 RandomNumberGenerator，默认数值不变。
- gate 调用前起独立审批窗口，同步前缀/Dispatcher/展示/人类等待都计入；`caller取消 > 截止 > 已观察活动 > 决定`，决定校验后再次复核；保持单读者并观察交接读 fault。
- store 自发取消归 key-unavailable；gate fault 与停机竞速保留 caller 取消；pending/success 本地写超时有明确拒绝码。本地 success 写出成功不是对端交付确认，写预算独立于已结束的审批窗口。

**客户端及永久回归**

- `ControlClientConnector.ConnectAndAuthenticateAsync(...)` 内部独占 TLS→hello→认证全链；challenge 的 device/pin 与冻结目标/实际出示证书对齐；client transcript 绑定实际 presentedPin；grant 与 serverProof 独立门禁，允许 Control→ViewOnly，不允许越权。
- 只有验证完成才返回 `AuthenticatedControlSession`；公开面只有身份、权限、sessionId、shortCode 与 Dispose，无裸流/token/输入接口。serverProof 失败固定文案为「远端身份验证失败，可能是错误密码或伪造设备广播」。失败/取消关闭连接。
- key 在首次 await 前复制，finally 清私有副本；会话拥有独立 token 并在释放时清零。`AuthSuccessFrame` 严格 canonical token 使用栈临时缓冲，`FrameReader` 失败时清未交出的 payload 数组。
- 客户端 machine 从 hello 本地写完起算；challenge 提示只收窄；一次合法 pending 接受后开始独立 approval；重复 pending 拒绝，prefix/payload 均受外层余量约束。解析、MAC 和会话构造后均做单调后置检查。
- 服务端新增 68 例（970 基线），客户端/帧与读失败清理再增 66 例，合计相对 902 增 134；Transport 485→619。测试混合真实回环 TLS、独立 HMAC 对端、可控 clock、纯判定器、替身 I/O；不混称为两机或真实 Dispatcher 验收。

**真实变异验证**

- 服务端最终五项：MAC 最后检查 2红/2绿；`>=` 边界 1红；审批末次检查 1红/1绿；迟到 key 清零 1红；准入保留 1红。每项构建成功，三个源文件恢复哈希一致，随后重新构建测试。
- 客户端首轮 01–09、重放 11–19，各九项：serverProof（4红）、实际 grant 绑定（2红）、overgrant（2红）、challenge device（1红）、实际 pin（1红）、MAC 后 deadline（2红）、重复 pending（1红）、token canonical（7红）、失败 payload 清零（4红）。每轮总计 24红/16绿，逐项恢复 40/40 绿；是执行次数，不是去重测试数。
- 首轮发现并修正测试 clock 观察点：到期事件放在权限/MAC 前采样之后，本次返回旧采样；删被测 MAC 后检查也不能删掉到期事件。正确 proof 的两例仍由会话构造后 deadline 兜底挡住，属于纵深防御，不冒称单点独立覆盖。测试依赖具名采样阶段，未来插入取时点须复核。
- 客户端重放前后整个 src/tests 的 181 个文件集及 SHA-256 一致；临时 device 变异触发过 1 条空性警告，恢复及最终构建均无警告。没有把编译错误当命中。

**恢复后的最终全量验证（本机实跑）**

| 项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 125 | 125 |
| Protocol | 223 | 223 |
| Security | 66 | 66 |
| Integration | 3 | 3 |
| Transport | 619 | 619 |
| 合计 | **1036** | **1036** |

两种配置 build 均 **0警告/0错误**；test 均 **0失败/0跳过**。日志为 `outputs/m4-deadline-review/final-{debug,release}-{build,tests}.log`。`git diff --check` 通过；src/tests 无临时变异标记；主助手重新计算三产品文件 SHA 与重放清单一致。

**Last code commit：`bc02a0c`。Working tree at validation：上述验证对应此提交的完整代码/测试树；验证后仅文档/本地证据整理，未改产品或测试。** 所有日志/原始诊断与压缩包保留本机 outputs，不当源码入库；新交付 `M4_STAGE4_VALIDATION.txt` 与 `M4_STAGE4_VALIDATION_EVIDENCE.zip`，区别于旧诊断 ZIP。

**明确未证明或未完成**

1. 普通 CTS 不保证中断永不返回的同步 store/gate/取消回调；异常隔离不是阻塞隔离。有界 loader 只限该 context 的 store，不扩大到任意本机工作。
2. DTO 的不可变 Base64 string、JSON/FrameWriter/TLS 内部副本不在显式清零保证内；不能声称全进程无秘密残留。
3. 客户端只消费首个终帧，未来重复 success 属后续协议消费者；批准前短码目前无公开过程通知，不能假装现有成功返回值支持它。
4. 验收器 tools、本体 App 和权威规格未改；未跑真实 WPF Dispatcher、GUI/两机验收或数值定案实验；网络、防火墙、真实用户密钥未作操作。

**阶段 4 结束时的阶段 5 续作点（历史，现状见 §18.11）**

- 已定可直接做：真实 `DpapiAccessSecretStore` 复用现有 vault；Host 级共享认证 context；消费 handoff 并 await auth；已有最小审批字段/按钮、异步 Dispatcher 和请求 ID 绑定；client success 改走高层入口。旧 pin-mismatch/timeout/slow-dribble 保留原低层语义。
- 成功证据改为客户端已验证会话 + Host 结果的 sessionId 配对，不能继续以 hello 后 EOF/PreAuthenticated 判成功；不公开 token/流，不把认证异常误分为 TLS 握手失败。
- 待用户确认：仅验收器「本机 key 显式查看 + 对端 key 遮挡输入、不落盘」是否采用；本轮是否要求批准前双端显示/比较短码（需要最小非秘密过程通知 API）。不重问已定的最小审批面及接受时刻。
- headless 无审批面不得自动批准；缺目标真实 deviceId/key 不得降级伪成功。现有“停止监听=中止作废”保持，不静默变成正常通过按钮。

**2026-09-23 续记：阶段 5 实施前外部评审材料**

用户要求直接编写可转发 prompt。材料位于 `outputs/m4-stage5-review/`：`M4_STAGE5_ACCEPTANCE_REVIEW_PROMPT.md` 聚焦批准前短码通知、真实 WPF 审批生命周期、密钥显示/输入、会话保持与两端证据；配套 `M4_STAGE5_ACCEPTANCE_REVIEW_EVIDENCE.zip` 收录当前 src/tests、完整验收器源码、合同及上一轮原始验证证据，并附逐文件 SHA-256 清单。材料不包含真实密钥、PFX、DPAPI 文件或产品数据；不自动外发。

代码基线仍为 `bc02a0c`，本轮未修改 src/tests/tools、未重跑构建测试、未操作 GUI/网络/真实密钥。两项交互仍是待审推荐，不因编写 prompt 而视为用户批准；下一步由用户转发材料并贴回评审，再核对源码事实与最小方案，不重复转发旧时限评审、不重问已定审批接受时刻。阶段 5 接线和真实 Dispatcher/GUI/两机验收仍未开始。

**2026-09-23 授权更新（覆盖上一续记的等待点）**：用户明确“国外模型暂时用不了了，你现在全权继续下去吧”。不再等待外部评审，按最小方案推进：验收器本机密钥显式查看、对端遮挡输入且不持久化；批准前单次非秘密短码通知；有界审批收件箱，取消不依赖 Dispatcher。不会因此变更线上帧、安全边界或静默改网。先实现并验证通知/收件箱，再接双端角色及界面；GUI 人工检查、两机验收仍待后续实测。

### 18.11 阶段 5 认证验收器接线与自动化收口（2026-09-23）

**结论**：实现与自动化完成，**M4 人工 GUI/两机验收未运行，整体仍进行中**。不再等待外部模型。产品 `LanRemote.App` 尚不能看屏或键鼠控制；本轮没有运行网络准备/撤销、没有读取真实访问密钥、没有修改原始规格。

#### A. 提交与验证树

- **Last code commit：`7a9d199`**（测试断言等价改写，消除 8 条 xUnit2031）；阶段 5 主实现 `6c7a15b`，28 files / +5317 / −999。
- **Working tree at validation**：最终双配置构建/测试对应 `7a9d199` 的产品及测试树。此前 `6c7a15b` 的 1190 tests 已通过，但原始 build `08/10` 存在 8 条测试分析器警告；不得将其写为零警告。修正不改变测试数据、断言条件或产品行为，重新执行完整 Debug/Release，而非只看增量 build。
- 原始证据在 `outputs/m4-stage5-validation/`；该目录为本地交付/证据目录，不随普通源码提交。提交后文档独立记账，不把 HEAD hash 写入本文件。

#### B. 实现边界

1. **批准前通知**：`ControlClientApprovalPending` 仅公开 SessionId、ShortCode、AcceptedAtTimestamp、ApprovalWindow；首个合法 pending 后通知一次。同步回调前后及异常路径复核原始单调窗口，耗时不补回；异常脱敏、取消/超时优先。不提前公开已认证会话、token、stream 或输入接口。
2. **审批**：`LocalApprovalInbox` 每 run 容量 3，RequestId + 独立 Generation 防迟到点击；快照拉取，不堆 Dispatcher。Stop/Dispose 永久关闭，取消不依赖 UI；收件箱接受决定不等于 Transport 接受授权。
3. **密钥与关窗**：PasswordBox 输入不持久化；只在活跃 Host 显式确认查看本机 key，原始单调窗口 15 秒、失焦/隐藏/停止清显示。context 单一 WorkLease 追踪附属读取，停机阻止新工作并等待旧工作清理、清零、故障记账后才释放 context/结算。普通 CTS 不保证强杀永不返回的同步工作，不保证擦除不可变 string/WPF 内部副本。
4. **双端认证**：Host 共享真实 store、限流、独立 3/1 待批配额与 registry，消费 handoff 并 await 认证/保持。Client success 在低层 TLS 前分流到高层连接器，绑定实际身份、校验 serverProof/权限后持有至少 5 秒，再 Dispose。无 key/占位身份/无审批 UI 不自动降级成功；headless 无秘密 argv。
5. **证据**：Host 单个 100ms sampler，只追踪当前活动会话。成功要求正向样本跨度 >=4s、最大间隔及结束到最后正向样本 <=500ms、已注销且非 Host 强关；缺样本、无成功、强关为 UNMET。成功按完整 SessionId 配对；旧负例按四元组。采样不证明间隙内每个瞬间在线，保持结束也不区分 EOF/RST/越界字节，需客户端主动 Dispose 证据。
6. **结算/GUI**：幂等 Complete 在资源清理后写唯一 footer；append 失败和 footer 自身失败都不可冒充 PASS。优先级 `INVALID_RUN > HARNESS_ERROR > FAIL > UNMET > PASS`。单 DispatcherTimer 分批拉日志，显示限长但复制完整日志；Host GUI 180 秒自然结算，提前停止/关窗仍作废，等待超过 8 秒只提示不强杀。

#### C. 实测与变异

最终构建日志 `14-clean-build-debug.log` / `16-clean-build-release.log`：均 **0 警告 / 0 错误**。
最终测试日志 `15-clean-test-debug.log` / `17-clean-test-release.log`：

| 测试项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 125 | 125 |
| Protocol | 223 | 223 |
| Integration | 3 | 3 |
| Security | 66 | 66 |
| Transport | 633 | 633 |
| Acceptance | 140 | 140 |
| 合计 | **1190** | **1190** |

两配置均 **0 FAIL / 0 SKIP**。相对阶段 4 的 1036 新增 154（Transport +14、Acceptance +140）。真实 TLS 接线四例调用实际 Host handler 与高层客户端，覆盖降权/自然释放、Host 强关、错 key、拒绝；STA Dispatcher 卡住时后台取消、WorkLease/RunState、真实独占文件锁制造日志失败均有测试。**没有启动人工视觉窗口，不把 STA/回环实测当作两机/DPI验收。**

变异目录 `mutations/`：8 项均成功构建后运行期变红，恢复同一目标测试全绿，survived=0，编译失败冒充 kill=0。

| 编号 | 目标 | 红测 PASS/FAIL | 恢复 PASS/FAIL |
| --- | --- | ---: | ---: |
| M01 | 重复 pending 通知 | 0/1 | 1/0 |
| M02 | 通知异常原文外泄 | 4/1 | 5/0 |
| M03 | RequestId+Generation 保护（复合） | 0/3 | 3/0 |
| M04 | ViewOnly→Control 越权 | 9/1 | 10/0 |
| M05 | 提交/交接取消复核（复合） | 0/2 | 2/0 |
| M06 | 样本不足假 PASS | 0/2 | 2/0 |
| M07 | 忽略 Host 强关 | 0/2 | 2/0 |
| M08 | 日志失败位失效 | 0/2 | 2/0 |

只读复核逐项 build/test/exit/hash 日志：7 项直接显示目标断言失败；M02 原异常外泄由夹具清理重新抛出，遮蔽原始断言诊断，**只能记运行期 kill，不宣称其日志直接展示了断言失败**。M03/M05 不是对每处检查的独立单点证明。目录未保存每项 patch/测试源码哈希，不能仅凭源 SHA 重建全部变异过程。四个目标源文件当前 SHA 与 baseline/final/各轮 green 一致；全部产品变异已恢复。8 为变异数，不是14个失败用例数；最后客户端定向67、Acceptance140均恢复通过。

#### D. 候选包与人工验收待办

最终发布日志 `outputs/m4-stage5-validation/18-package-final.log`。候选包：

- `outputs/m4-stage5-validation/LanRemote-0.1.0-m2-m4-acceptance-win-x64.zip`
- **60,265,589 bytes（约57.5 MiB）**，265个唯一扁平成员；CRC、全体成员与发布目录逐字节一致、说明文件一致均通过。
- **SHA-256：`4972edee30e7d213c9805c179c0e69f0f3a532fe261073e2207ab67932308f19`**。
- EXE 为 AMD64、PE Subsystem=2（GUI），程序集包含提交 `7a9d199`；这只是静态入口/版本验证，**不是人工启动和窗口渲染通过**。
- 所有包内 ps1 有 UTF-8 BOM；未含 PFX/P12/key/secrets.bin 文件名。说明为包内 `START-HERE.txt`，外置 `M4-验收说明.txt`；无须安装 .NET，但必须整包解压。
- 此包替代前两次打包，不能沿用第一次 `e41de8b5…` 的 SHA。打包之后产品/测试/脚本与提交无差异。

使用本轮自包含候选包；整包解压，双击 `LanRemote.Acceptance.exe`。两台同子网 RFC1918 Windows 设备；人工检查布局/DPI、显式查看/失焦清空、pending 短码、批准/拒绝/降权、错误提示与关窗。先正常 success+三个低层专项，等待 Host 180秒自然结束，提交双端完整日志，按 SessionId/四元组配对。拒绝、错 key、主动停止等负例单独运行，不与正常通过轮混淆。不要发真实 key/私钥或含 key 的截图。

**历史包已停用（2026-09-24）**：上述§18.11候选包及当时的准备A/B流程均不可继续使用。确认/UAC不构成改网豁免，现行包与不改网流程以§18.13及ADR-045为准。不重用历史M3 EOF成功判据；人工通过之前不勾选M4完成。真实serverProof错误显示若未制造对应场景，仍标为未验，不能用一般拒绝提示替代。

#### E. 剩余范围与条件化估算

当前剩余 M4 人工收尾，以及 M5 视频闭环、M6 多屏/画质、M7 键鼠和安全闸门、M8 多会话、M9 可靠性/UX/诊断、M10 发布集成；M11 后置优化。测试数不代表产品完成百分比。

从当前状态估算：主屏只看最小版约1–3周，基本控制版约3–6周，既定范围自用稳定版约6–12周或更久。假设范围稳定、持续投入开发、真机及时配合；这是工程工作量估算，不是承诺日历交付日期，也不代表对话结束后会持续后台开发。首条真实视频闭环后重估。当前需要用户协助的是两机 GUI/认证验收，而非外部模型转发。

### 18.12 热点真机验收受阻与不改网硬约束（2026-09-24）

本节保留事故发生时的诊断快照，不是新代码验证报告；后续已完成修复及本机验证见§18.13，下文“尚未”仅指当时。

**用户重申原始需求（不是新增功能）**：同一局域网中安装并运行本软件的机器应自动发现；软件适应并使用既有网络，不更改系统网络协议配置，不把DHCP改成静态、不要求设置实验IP。A/B只是设备/测试角色，不对应固定地址。发现失败应修复软件识别与通信，或如实诊断隔离/拦截原因，不得以改网作为普通使用前提。当前场景为A开热点、B连A热点；双方原有上网必须保留，任何一端因软件/验收断网（含短暂）都不接受。确认框/UAC、“只改实验环境”或“之后可撤销”不构成豁免。不能通过改 DHCP、IP、默认网关、DNS、路由、热点/ICS或网络类别凑验收。后续网络恢复必须先核对当前状态与原状态，解释影响并获单独许可，不能默认执行 lab-undo。

**用户提供日志（不是助手现场复测）**：
- 两端均有 `[GUI] M4 窗口渲染完成`，1000×940；只证明渲染日志存在，不证明全部布局/DPI/密钥/审批功能通过。两端 harness 与 Transport SHA分别一致。
- A：原上游以太网 if6 为 `172.100.166.220/24`、DHCP；日志还列热点下游 `本地连接* 2 = 192.168.137.1/24`。原 info 无合格网卡。10:20准备A选择上游以太网，改静态、附加 `192.168.1.10/24`；用户报告随后B无法上网，具体ICS/DNS/转发失效环节未定位。
- B：原WLAN if23为 `192.168.137.141/24`、网关 `192.168.137.1`，info已合格。准备B仍转静态、附加 `192.168.1.20/24`，把Public改Private。随后同接口两个binding，在 `LanDiscoveryService.CreateReceiver` 的 `AddMembership`（line667）反复抛10022，Host及Client四场景均HARNESS_ERROR，尚未进入认证。
- A正常Host轮 `081e04c0` 为UNMET，`sessionHandled=0`；另一轮主动停止INVALID_RUN。lab/info的PASS只反映本地地址核验，不证明热点可用、双方互通、持续上网或认证通过。相同192.168.1/24被写到A上游和B下游，不会自动把两条链路合成同一广播域。

**只读源码结论与待证事项**：`VirtualAdapterFilter` 的 `virtual` 匹配会排除描述带Wi-Fi Direct Virtual Adapter的接口；但日志没给A热点完整属性，尚不能确认实际命中分支。`CreateReceiver` 确实对每个IP binding在同一socket加入同一组播组，未按接口身份去重；B重复加组是高可信10022成因假设，缺逐次加入记录/受控复现，不能写成已定案。

**修复方向（尚未实施）**：撤下普通验收的lab改网引导；只读识别/核实热点下游接口并做窄范围支持，不放开所有虚拟/VPN接口；receiver按接口身份去重，保留各IP真实掩码与发送/监听binding；补诊断和回归。继续严格RFC1918、同子网、TLS/pinning/HMAC。应使用现有热点137网段，不硬编码热点IP，不通过改系统网络回避代码缺陷。

**诊断轮执行边界（历史）**：当时仅分析源码与用户日志，不改网、不默认Undo。后续实现及实测如下。

### 18.13 现网热点修复、验证与候选包（2026-09-24）

**结论**：不改网修复主提交 `bc8cf48`，拒绝提示UTF-8修正 `72bfb93`。A侧已在既有热点上实跑发现/监听并自然结束，未改系统网络；M4仍缺修复版GUI/两机认证及双方联网持续正常的人工证据，不能进入M5。

**实现**（ADR-045；旧ADR的改IP/网络配置默认计划已覆盖，不得按历史恢复）：
- WFD窄例外：Wireless80211、正接口索引、系统Description精确匹配（可带无前导零ASCII正整数实例号）；仅豁免virtual，其余VPN/Hyper-V等词继续拒绝。不硬编码137网段、不把名称当身份、不声称识别ICS角色。
- `MulticastMembership.JoinUniqueInterfaces`：join前验证index/Id完整一致，每接口一次；全部地址binding、真实掩码和逐地址sender保留。receiver失败按接口/地址/组/SocketError/NativeError记账，释放socket并回滚应用资源，不改网络补救。
- 删除GUI准备/撤销按钮及执行链；解析器、Runner、旧role/elevated壳均拒绝，普通info/host/client保持。旧PS参数后无条件throw，保留历史正文但不可执行；旧M2打包入口拒绝。
- 新包每次发布到独立空目录，禁止SKIP，不删除旧产物；递归过滤ps1/cmd/bat及旧START-HERE，只附带一份明确选择的现网说明。打开日志目录功能保留，无无关改动。
- 包级实跑发现旧命令解析拒绝在RunAsync之前，stdout仍系统代码页；补WriteUsage重定向UTF-8后重新双配置验证、提交及出包，原失败日志保留。

**自动化实测**：Debug/Release各 **1305 PASS / 0 FAIL / 0 SKIP**；Core125、Protocol299、Integration3、Security66、Transport633、Acceptance179；比1190增加115（筛选64、membership12、拒绝/只读39）。构建均0警告/0错误。最终日志 `outputs/m4-network-fix/14..17`；Python打包/旧脚本静态回归 **10 PASS**（08）。没有实际运行旧PS或UAC。

**三项真实变异**：禁WFD例外7红→7绿；WFD绕过其它token 11红→11绿；取消membership去重2红→2绿，六次独立构建均成功，红均运行期失败。baseline/mutant/测试源码哈希/逐例TRX均保存，最终字节恢复一致。M03原证据脚本错误期待SetSocketOption，实际堆栈SetMulticastOption；保留原success=false并加独立复核，不篡改原始证据。M02红证据来自名称冲突；M03为双地址回调计数+同回环binding真实socket重复加组的复合证明，不能当作B真实双地址10022完整复现。证据 `outputs/m4-network-fix/mutations/`。

**A侧现网实测**：
- 系统只读核实：if13 `Microsoft Wi-Fi Direct Virtual Adapter #2`，Wireless80211/Up，`192.168.137.1/24`；if6上游 `172.100.166.220/24` DHCP，未出现实验1.10；WSL仍被筛掉。
- Release DLL run `1d0fd025`（09）：`qualifiedNic=192.168.137.1`，组播加入if13成功，TCP45873绑定137.1，3秒自然结束，无handler/cleanup故障。此轮[BUILD] harness是启动宿主dotnet.exe，不能拿它当验收程序集哈希。
- 最终包内EXE再次运行同一现网检查（19），组播和TCP绑定成功、自然结束；旧prepare-lab+elevated参数真实执行被拒绝，退3且中文UTF-8可解（20）。无对端认证，所以Host正确为 **UNMET/退2**，不是PASS。
- 两次只读快照（network-before/after）对所列Up接口、Preferred IPv4/掩码、DHCP、默认路由和DNS逐结构相等；快照在首批现网实跑前后采集，不是持续监控，也不是最终包重跑后再采样。未检查B、未证明互联网连续可用或恢复，不能外推。

**最终交付**：`outputs/m4-network-fix/LanRemote-0.1.0-m2-m4-acceptance-win-x64.zip`，264唯一扁平成员，60,242,991 bytes（约57.5MiB）；SHA256 `9d947a68cdd127dd73ca46f9cdbf6e7e9eec08a52c4493ed84bc33ee4caa434e`。CRC通过、逐文件与此次publish一致，唯一START-HERE.txt与`M4-现网验收说明.txt`相同，无脚本；EXE AMD64 GUI subsystem2，托管程序集包含完整72bfb93提交标记。发布日志18，`package-verification.json`保存检验结果。静态PE/无窗口实跑不冒称新GUI布局已人工通过。

**下一关**：旧包停止使用，双方原有联网正常后换本包，各自解压新目录并双击。只读自检→Host180秒→查看/安全交付密钥→Client四场景及审批→Host自然结算→两端完整SessionId/负例四元组配对，同时记录双方使用前/中/后联网无影响。若旧配置仍造成断网，暂停并单独核实恢复，不使用旧Undo。当前软件仍未实现看屏和键鼠控制。

### 18.14 双机审批超时及界面修正（2026-09-24）

**现场结论**：用户粘贴A client `c96401d1`（07:32:59Z）及B host `b5b519b8`（07:32:03Z）。两端旧现网包EXE/Transport哈希相同，地址A `192.168.137.1/24`、B `192.168.137.141/24`；B if23组播加入成功并发现A，A也发现B并完成真实TLS。此次不再是AddMembership启动故障；未证明B原双地址现场的全部因果，也不能证明互联网持续正常。

- success：A `client-approval-timeout`；B conn#1/session `82bce40d-1580-4b78-8865-6d1d57cb3c09`，`auth-approval-timeout`、elapsed60097.729ms、authenticated=False、observedSamples=0。无客户端成功SessionId，本条只能按同轮顺序/唯一认证尝试关联，不能伪称完整SessionId双端配对。按状态机代码，已越过clientProof检查进入本机审批，但没有serverProof成功及已认证会话证据。
- pin-mismatch：A按pin拒绝，B conn#2的TLS13 pre-auth-eof与其相容；A未记录本地端口，只能顺序关联，非严格四元组配对。
- timeout：A本地 `192.168.137.1:63919`→B `.141:45873`，5011ms EOF；B conn#3 peer同端口，pre-auth-timeout，5013.013ms，精确配对。
- slow-dribble：A本地`:53730`，sent3/4、5009ms EOF；B conn#4 peer同端口，pre-auth-timeout、5007.872ms，精确配对。
- B自然结束（非操作员中止），sessionHandled4、partitionOk=True；handler/registry/pending=0，handlerFaults0、cleanupFaultFalse、firstStopAllFinishedWithinBudgetTrue。结果A FAIL/B UNMET合理，不能改判M4通过。B的“设备离线”是发现记录过期，不证明任一端互联网断网。

**明确问题与推断边界**：原审批区处于上方ScrollViewer最底部，存在不可见风险；StartRole写“无待批请求”后RefreshApprovalList未在首条入队时更新该文案，确定会产生误导。旧UI只记成功提交，没有失败点击/拉取记录；不能推断用户没点，也不能将界面问题断言为本次唯一原因。

**最小修正**（代码 `67c7d7e`，4 files）：
- 审批区移到根Grid独立行，待批数量/“先选中”提示独立于最后动作；列表限高、长名称行省略，手动选择规则保持。
- 只对最多3条快照差集记录OBSERVED/REMOVED（含RequestId、Generation、SessionId、ConnectionId、UTC），不积累历史ID；拉取不等于实际可见，移除不推断原因。
- 提交成功/失败均在UI更新前记submitted结果，关闭/故障或未产生Click不属完整物理点击审计。submitted=True不等于授权；无密钥/新网络动作。
- 保留旧run快照owner，FinishRole及StopCurrentRun同步清理列表/提示并归属旧日志，避免跨轮丢失或串记。
- 新增internal隔离日志目录构造给真实WPF测试；不触用户gui.log、身份或网络。未改Transport/协议、审批60秒、pending配额、RequestId+Generation、防御顺序，不自动选中/批准。

**实测**：新增24项真实STA/WPF控件测试（含Click路由、实际Inbox决定、失败旧代、重复刷新去重、FinishRole/StopCurrentRun、同源App.xaml样式与800×640离屏布局、64中文名）。初版4个xUnit警告已修；长名称验证最初错误读取TextBlock.Text，改查真实绑定Run.Text后通过，原失败日志保留。离屏布局不等于真屏/DPI/鼠标可达验证。
- 定向24 PASS；临时把队列计数改0，目标测试真正1 FAIL（实际有1条却显示无待批）；恢复后双配置全绿，变异测试未改。
- Debug/Release各 **1329 PASS / 0 FAIL / 0 SKIP**：Core125、Protocol299、Integration3、Security66、Transport633、Acceptance203；构建均0警告错误。日志 `outputs/m4-approval-ui/09..12`，变异07/08。

**新包**：`outputs/m4-approval-ui/LanRemote-0.1.0-m2-m4-acceptance-win-x64.zip`，264唯一扁平成员，60,244,834 bytes，SHA256 `444f207295e886ad824dc29ec346586172abd6212476e572498f836f157427c8`。与独立publish逐文件相同、CRC通过、唯一新手册、无脚本、AMD64 GUI subsystem2、程序集含67c7d7e完整提交。发布目录artifacts/m4-acceptance-r_ljlyg1；包内同源EXE只读info实跑a589d94c通过（本地限定），未替换用户安装目录、未开启新GUI或发起真实审批。包级日志14。

**用户下一步（当时记录，主流程现已完成，见§18.15）**：确认双方当前联网正常；两台换新包到新目录。B开始180秒监听，安全交付密钥；A开始四场景后B在60秒内手工选中条目、核对两端短码并“批准请求权限”，等自然结束交两端日志。若仍超时需反馈条目出现/选择/按钮可用/点击后提示，并避开密钥截图。不为过测试延长期限或改网络；成功认证、降权/拒绝与完整GUI人工验收仍未完成。

### 18.15 修正版两机认证主流程通过（2026-09-24）

**证据来源与范围**：用户本轮粘贴的A info `0728189f` / client `31255177`，B info `3b99088a` / host `d8fd8b0d`日志；不是助手重新运行或从B磁盘采集的原始文件。A client开始09:24:23.259Z，B host开始09:23:43.100Z。两端EXE SHA256均为 `0E5421A658B3EF9AF06733E07E2D29A6F375654EF58AF3DECAC4A473F14B83FD`，Transport均为 `AE90E02431DF674605A15E7B44897F92AF2BD2FA19BC16BB8BFD53F6038F4434`，与§18.14已验证包一致；只核对了日志所列两个组件，不冒称B所有264文件均重新校验。

**结论**：本轮真实两机认证主流程及保持/自然注销PASS。A四场景全部PASS；B最终PASS、退出0。原审批超时不再是当前阻断，但不将它扩展为完整M4人工清单全通过。没有新产品代码或新包；`Last code commit`仍为`67c7d7e`。此前双配置1329结果保持，本次未运行build/test，只有日志值/时间差计算与文档核对。

#### A. 发现、审批与认证成功的精确配对

- A `192.168.137.1/24`（热点if13）、B `192.168.137.141/24`（WLAN if23）；B组播加入成功并发现A，A解析到B并连接。不能由此反推DHCP/DNS未变或双方互联网持续正常。
- 完整SessionId：`6d3c41f2-5bb5-4f1d-acdd-a091a755baea`，在A AUTH/CORRELATE/HOLD/DISPOSE/RESULT与B AUTH-BEGIN/UI/SESSION/RESULT中一致；B connectionId `be0a9f6b-849b-4d86-8c7e-d21b6235be6f`，peer `192.168.137.1:60592`。A高层日志未给本地端口，成功场景使用SessionId，而不是伪称四元组双端齐全。
- 审批RequestId `286fe033-ad6d-4e29-8d2f-cad8c2006ae8`、Generation `82a7d938-011a-4bf4-bc9a-59e0e4d4a699`：OBSERVED、APPROVAL及REMOVED均一致，并关联同SessionId/ConnectionId。
- B UI拉取09:24:25.2724095Z，提交09:25:09.5978545Z，`submitted=True decision=Approved grant=Control`；显示截止09:25:25.1970154Z。拉取至提交约44.325秒，提交距显示截止约15.599秒。只是UI记录时间差，不是物理点击时间或最终裁决的单独证明；随后A验证serverProof/B authenticated才是授权成功证据。
- A `serverProof=verified`、`grant=Control`、expectedPin与presentedPin一致，`localObjectHeldMs=5001`、同步Dispose完成。
- B `authenticated=True tracked=True observedSamples=50`，`observedSpanMs=4911.012 >=4000`，`maxObservedGapMs=110.472 <=500`，`endObservationGapMs=64.829 <=500`，`observationInterrupted=False`。
- B `deregisteredAtRunEnd=True hostForcedClose=False evidence=PASS`，终态`authenticated-ended-unregistered`。elapsed49504.298ms包含审批等待与会话保持，不能当作TLS握手耗时；不将采样证明写成所有间隙连续在线证明。

#### B. 三个负例及收尾

| 场景 | A实测 | B实测 | 配对结论 |
|---|---|---|---|
| pin-mismatch | AuthenticationException，明确pin-mismatch，PASS | conn#2 peer :55847，TLS13 pre-auth-eof，5.075ms，Closed | 与顺序和TLS13行为相容；A缺本地端口，不能唯一四元组配对，保留不确定性 |
| timeout | local .1:55848 → .141:45873，5001ms EOF，PASS | conn#3 peer .1:55848，pre-auth-timeout，5000.256ms | 四元组及拒绝原因精确配对 |
| slow-dribble | local .1:54266 → .141:45873，sent=3/4，5011ms EOF，PASS | conn#4 peer .1:54266，pre-auth-timeout，5009.746ms | 四元组及拒绝原因精确配对，未随字节重置期限 |

- B `sessionHandled=4`，桶为1认证自然注销+1前认证EOF+2前认证超时，`partitionOk=True`；`authenticatedEnded=1 qualifiedNaturalRelease=1 holdUnmet=0 hostForcedClose=0 registryViolations=0 trackingDropped=0`。
- B `firstStopAllFinishedWithinBudget=True firstStopUnfinishedConnections=0 acceptLoopsFinished=True activeHandlers=0 activeRegistry=0 pendingApprovals=0 handlerFaults=0 cleanupFault=False`；双方`aborted=False`、无后台故障，Host自然结算。
- A仍打印`M4 = PENDING-HOST-EVIDENCE`是单端不可宣布完成的设计；本轮B证据已供人工交叉核对，不修改程序日志来制造PASS。
- B的“设备离线”仅为发现记录过期，不能解释为互联网掉线；未观测TLS级拒绝、close_notify、EOF/RST区别的项目仍保留UNOBSERVABLE/UNOBSERVED。

#### C. 仍需人工补证，不扩大本轮通过范围

**用户本轮已明确补充确认**：针对“测试前、运行中和结束后两台日常上网是否正常，包括无短暂中断”选择“两台始终正常”；针对“审批条目和按钮显示正常且批准前核对两端短码一致”选择“显示正常且已核对”。这两项记为本轮人工通过，属于操作者观察，不是连续网络遥测；不再重复追问或要求同轮重跑。

沿用§18.11 D既定清单，不增加新的验收门槛。剩余：未覆盖的布局/DPI组合；显式查看密钥、遮挡/失焦/到期清空；独立降权为ViewOnly和拒绝请求、错key提示、关窗/主动停止。真实serverProof失败的固定文案仍需对应故障场景，不能由错key或pin拒绝提示替代。正常Control主流程不必无理由重跑，复用现包且不改网、不读出真实密钥；拒绝/错key/主动停止应单独记预期拒绝/无效运行，不能混作正常success轮失败或强改全局PASS。完整人工清单完成前，M4保持进行中，不进入M5。

本轮核对说明：`outputs/M4-两机认证核对-20260924.txt`。报告为用户粘贴日志的摘要与阈值核对，不是原始日志副本或新一轮执行结果。

### 18.16 降权主流程通过；拒绝行为已观测但Host轮作废（2026-09-24）

**来源与范围**：用户粘贴A info `a1672e19`、client `738fbcc1` / `aa9cf1bf` / `d1097199`，B info `535e7cf0`、host `67442a17` / `80722d29`。两端所列EXE/Transport SHA与§18.15同修正版一致；不外推完整目录逐文件校验。本次仅核对日志、只读复核`AcceptanceRun.MarkOperatorAbort`及Host结算规则并记账，没有改代码/网络、运行build/test或重打包。Last code commit保持`67c7d7e`，1329为此前自动化结果。

#### A. 有效降权轮：PASS

A `738fbcc1`（10:06:01.404Z）/ B `67442a17`（10:05:10.870Z），双方PASS/aborted=False/无后台故障。完整SessionId `f85e6d5f-4b3d-4dd8-aa93-101d74693997`在双端一致，B ConnectionId `5559c45c-d0e1-4a3e-a0f1-2050235dc488`，peer `192.168.137.1:61339`。

- A `requestedPermission=Control`；B UI `submitted=True decision=Approved grant=ViewOnly`；A最终`serverProof=verified grant=ViewOnly`。三者组合证明实际已验证会话权限降为ViewOnly，不是仅UI文字变化；不提前证明尚未实现的键鼠输入闸门或视频功能。
- A保持5001ms后同步释放。B `authenticated=True tracked=True observedSamples=50`，`observedSpanMs=4887.638 >=4000`，`maxObservedGapMs=110.118 <=500`，`endObservationGapMs=24.545 <=500`，`observationInterrupted=False`；`deregisteredAtRunEnd=True hostForcedClose=False evidence=PASS`，终态`authenticated-ended-unregistered`。
- timeout：A `.1:53080`→B`.141:45873`，EOF5008ms；B同peer `pre-auth-timeout`5009.101ms。slow-dribble：A`:59997`、sent3/4、EOF5011ms；B同peer `pre-auth-timeout`5011.865ms。两者精确四元组配对。
- pin-mismatch：A明确pin拒绝；B conn#2 peer`:53079`、TLS13 `pre-auth-eof`3.374ms，仅顺序关联，A无本地端口。不冒称全部负例唯一配对。
- B桶1认证自然注销+1前认证EOF+2前认证超时=4、partitionOk=True，authenticatedEnded=1、qualifiedNaturalRelease=1；第一次停止报告全部完成、handler/registry/pending均0、handlerFaults=0、cleanupFault=False。保持证据为采样，不是所有间隙连续在线证明。

#### B. A独立取消轮：INVALID_RUN，不污染上一轮

A `aa9cf1bf`开始10:08:27.561Z，UI中止控制端，连接阶段UNOBSERVED（未知1，未执行3），最终INVALID_RUN/aborted=True/退出4、无后台故障。没有peer、SessionId或本地端口，不能断言从未建连、把B监听窗口间隔当唯一原因，或配到后一个拒绝会话。此轮仅提供取消处理/作废标记的诊断观察，不计认证通过或活动会话取消专项通过，也不代表关窗测试。取消只作用于本run，不能推翻已完成的降权轮PASS。

#### C. 拒绝尝试：行为明确，仍不接受作废轮为专项通过

A `d1097199`开始10:08:56.902Z，B `80722d29`开始10:08:44.954Z。

- B RequestId `fe2cc27c-086e-45a7-bdbf-9d728910c06d`、Generation `da24e9c7-8ded-40c4-a11d-859756dee529`的UI提交为`Denied`、grant=-，并关联SessionId `d154d45e-b278-45d2-b531-aa2602a8da4b`及ConnectionId `cce8ad3f-3640-4827-934d-b3cc548848ec`。同会话`authenticated=False observedSamples=0`，终态`auth-rejected:auth-approval-denied`、Closed，peer`:53012`，elapsed3080.992ms。
- A `client-remote-authentication-failed`并显示“远端拒绝了认证或审批请求”，未得到已验证会话。A失败路径无SessionId/本地端口，只能按同轮目标及顺序关联B唯一拒绝尝试；不是严格SessionId/四元组双端配对，不能由该通用错误单独证明错key或serverProof失败。
- 后续timeout`:53639`，A5017ms/B5013.313ms；slow-dribble`:55986`，A3/4、5003ms/B5002.19ms，端点与原因相符。pin为A明确拒绝/B`:53638`TLS13 EOF，仅顺序关联。这些只作该作废轮诊断，不纳入有效验收PASS。
- B随后明确`UI 提前停止监听`；817样本、采样跨度81599.949ms，最终INVALID_RUN/aborted=True/退出4。虽停止前无活跃认证、最终handler/registry/pending全0、无后台/清理故障，也不能替代180秒自然结算。
- 现行`MarkOperatorAbort`整轮作废，Host最终Complete优先处理abort。**保留拒绝行为的诊断事实，不把本轮记为有效拒绝专项PASS，不套用旧M3收尾豁免，也不修改日志/判据。** A总FAIL是正常success场景被拒绝后的预期标签，不是新发现产品故障。

#### D. 下一步与边界

Control和降权不重跑、不重打包。仅补一轮有效拒绝：B开始180秒监听、A发起、B拒绝；A跑完，B自然结束，均不按提前停止。无其它流量且只有拒绝时，预期A总FAIL、B总UNMET、双方aborted=False；按`Denied`→`auth-approval-denied`、无认证会话、配对强度及清理判据判专项，不强求总PASS。若仍INVALID_RUN或有故障，不算通过。

原清单剩余密钥显示/遮挡/失焦/到期、错key、真实serverProof失败固定文案、未覆盖DPI及关窗/活动会话停止仍按实际覆盖记账。前一Control轮用户确认联网及审批界面正常的结果保留，不重复追问，但不冒称后续全部轮次互联网均被连续监测。M4整体仍进行中，不进入M5。

核对摘要交付：`outputs/M4-降权与拒绝核对-20260924.txt`；不是原始日志备份或新一轮执行结果。本节拒绝轮为作废历史；新的有效轮如下。

### 18.17 有效拒绝专项通过（2026-09-24）

**来源与结论**：用户粘贴A info `01b49b9c` / client `66ef28d0`及B info `711e6b19` / host `8bd52649`。A开始10:19:55.036Z，B开始10:19:07.296Z；两端日志列出的EXE/Transport SHA与§18.14修正版相同，未重新校验完整安装目录。按此前明确的拒绝专项判据，本轮PASS；程序实际总结果仍为A FAIL/退出1、B UNMET/退出2，均aborted=False、无后台故障。没有重新定义总PASS、没有重写原日志，§18.16作废轮也不改判。

- B UI `submitted=True decision=Denied grant=-`，同一Host会话的终态`auth-rejected:auth-approval-denied`，`authenticated=False observedSamples=0 returnedState=Closed`。SessionId `66b8111f-a9a6-4063-b5a2-9f42215ccd3e`，ConnectionId `a2667edb-5582-479e-9306-1971edd37933`，peer `192.168.137.1:58939`，elapsed1623.377ms。UI提交与Host终态相互印证，不只用submitted判断结果。
- A `client-remote-authentication-failed`、“远端拒绝了认证或审批请求”，未获得已验证会话。A失败路径没有SessionId/本地端口，按同轮目标和唯一拒绝尝试顺序关联B，不冒称严格双端SessionId或四元组配对；拒绝原因由B具体终态佐证，不是错key或serverProof失败专项证明。
- timeout：A `.1:58941`→B`.141:45873`、EOF5013ms；B同peer `pre-auth-timeout`5011.047ms。slow-dribble：A`:53079`、sent3/4、EOF5001ms；B同peer `pre-auth-timeout`5001.147ms。两个负例精确四元组配对。pin-mismatch为A明确pin拒绝、B conn#2`:58940`TLS13 EOF2.243ms，仅顺序关联。
- B 1800次采样，sampleSpanMs=179901.291；自然结束，无操作员中止。桶1审批拒绝+1前认证EOF+2前认证超时=4、partitionOk=True；authenticatedEnded=0、qualifiedNaturalRelease=0是拒绝轮预期，不填成成功保持证据。
- B firstStopAllFinishedWithinBudget=True、firstStopUnfinishedConnections=0、acceptLoopsFinished=True；activeHandlers/activeRegistry/pendingApprovals均0，handlerFaults=0、cleanupFault=False、registryViolations=0、trackingDropped=0。该清理和有效自然结束记录补齐了上轮缺口。

**进度与下一步**：Control、降权、拒绝三项均完成，不再要求用户重跑。前一Control轮联网与审批可用性/短码人工确认保留，不外推为本轮持续网络遥测。原清单剩余密钥显式显示/遮挡/失焦/到期清空、错key、真实serverProof失败固定文案、关窗/活动会话停止与未覆盖布局/DPI仍未全部验证，M4整体保持进行中，不进入M5。不能把普通远端拒绝提示替代真实serverProof失败显示。

本轮仅日志核对与文档更新，无产品代码/网络变更、无build/test或重打包；Last code commit仍67c7d7e，此前双配置1329结果不冒称本次重跑。摘要：`outputs/M4-有效拒绝核对-20260924.txt`。

### 18.18 B 密钥界面六项人工确认通过（2026-09-24）

**来源**：助手明确要求只在B当前审批修正版窗口检查六项，A不发起连接；用户随后回复“各项正常”。据此记录以下人工观察通过，不冒称助手现场操作或新增自动化测试：

1. 未主动查看前，密钥区域没有明文。
2. 点击查看后在确认框取消，不显示密钥。
3. 确认查看后保持前台，约15秒窗口到期明文清空，未自行重新显示。
4. 再次查看后切至其他窗口，返回时密钥已清空。
5. 再次查看后点击“隐藏密钥 / 取消查看”，显示立即清空。
6. B当前显示缩放下，相关文字和按钮无遮挡且可点击。

**边界**：仅记上述六项；未取得本轮runId、精确计时或最终结算日志，不据此补认证/自然结束证据。不外推所有DPI、A侧输入框遮挡、最小化、停机/关窗清理或不可变字符串/WPF内存副本擦除；不读取、记录或索取真实密钥。前三类权限专项和这六项均不重跑。

**下一步**：独立错key轮同时观察A输入框遮挡与真实错误显示。使用合法格式但不同的密钥输入，不改B存储；预期A通用远端拒绝，B具体终态`auth-proof-mismatch`、无待批及无已认证会话，需双端日志确认，当前仍未运行。普通拒绝不能替代真实serverProof失败提示；后者及关窗/活动会话停止、原范围内尚未覆盖布局项仍待验。M4整体进行中，不进入M5。

本次仅更新HANDOFF、记忆与任务状态；无产品代码/网络变更，无build/test或重打包。Last code commit仍为`67c7d7e`，原1329项为此前实测；继续使用现有审批修正版。

### 18.19 错密钥专项通过（2026-09-28，同轮B尾部已补齐）

以下保留首次核对的缺口记录；本节末尾为补齐后的最终判定。

**来源**：用户粘贴A info `d0d87345` / client `509bbe18`（01:56:48.358Z开始）、B info `682454e0` / host `ad5ed4cc`（01:55:38.912Z开始），并明确“输入遮挡正常”。仍为137.1/137.141同/24；日志列出的EXE与Transport两个哈希均匹配§18.14审批修正版，不声称完整安装目录复核。不是助手现场重跑。

- **已证实的拒绝行为**：B conn#1终态`auth-rejected:auth-proof-mismatch`，`authenticated=False observedSamples=0 returnedState=Closed`，elapsed809.123ms；SessionId=`32810293-01b9-430c-8385-5c192de8c40d`，ConnectionId=`77f13ff2-d6c3-4588-8360-fbc6c1c482a0`，peer=`192.168.137.1:50858`。这是proof不匹配，不是本机审批Denied或审批超时；该会话未认证。没有待批UI日志，但不由“未见日志”独立推断所有UI瞬态。
- A实际输出`client-remote-authentication-failed`及“远端拒绝了认证或审批请求。”，完整结算`FAIL / exitCode=1 / aborted=False / background=无故障`，符合错key轮客户端预期。失败路径无SessionId/本地端口，双端该拒绝仅按目标和同轮唯一认证尝试顺序关联；Host内部ID一致，不夸大为双端精确ID配对。
- **输入遮挡**：用户明确确认，A输入框遮挡人工项通过，不重问。错误文案目前已见实际日志输出，不额外声称用户单独确认了弹窗或所有提示控件视觉效果；也不是serverProof校验失败固定文案验收。
- **低层场景**：pin-mismatch为A明确证书pin拒绝，B conn#2 peer`:52453`、TLS13 pre-auth-eof、3.794ms，仅顺序关联。timeout为A local`:52454`→B`.141:45873`、EOF5014ms，B同peer pre-auth-timeout5015.098ms，精确配对。slow-dribble为A`:57251`、3/4字节、EOF5013ms，B同peer pre-auth-timeout5013.003ms，精确配对。
- **未完成证据**：B本次粘贴在“设备离线”结束，缺`[HOST][BUCKETS]`、`[HOST][EVIDENCE]`、`[HOST][SUMMARY]`、最终`[RESULT]`及`RUN COMPLETE`。目前只看到4条handler终态，不把它们替代最终partitionOk/清理报告。B最终outcome、aborted、background、180秒自然完成、firstStop和全局handler/registry/pending归零均未观测；不能预填UNMET或PASS。“设备离线”是发现记录过期，既不等于监听结束，也不证明互联网断网。

**下一步**：仅索取B同一run `ad5ed4cc`从上述离线行之后到`RUN COMPLETE`的尾部；若窗口输出已丢失，取B本轮日志`C:\Users\MR\AppData\Local\Temp\lanremote-m3-acceptance\m3-host-20260928-015538-ad5ed4cc.log`的尾部。不要立即重跑或让用户补A全文。错key行为符合预期、整轮有效收尾待核；既往Control/降权/拒绝/B密钥六项和A输入遮挡均不重复。真实serverProof失败显示、关窗/活动会话停止等仍未验，不进入M5。

本次只核对用户日志、更新进度与记忆；无产品代码/网络变更，无build/test、未重打包，Last code commit仍`67c7d7e`，此前1329项不冒称本次执行。

#### 同轮尾部补齐后的最终判定

用户随后提交B完整日志：仍为host `ad5ed4cc`，开始时间、PID15096、SessionId/ConnectionId及四条终态与首次片段一致，是补齐同一轮，不是新测试。

- `authenticatedActiveBeforeStop=0`；`sessionHandled=4 active=0 sum=4 partitionOk=True`，桶为1 proof-mismatch + 1 pre-auth-eof + 2 pre-auth-timeout。
- `registrySamples=1800 sampleSpanMs=179889.598 maxSampleGapMs=114.818`，正常完成180秒监听窗口。`trackedActive=0 trackingDropped=0 authenticatedEnded=0 qualifiedNaturalRelease=0 holdUnmet=0 hostForcedClose=0 registryViolations=0`；拒绝轮无已认证会话符合预期，不把零样本当成功保持证据。
- `firstStopAllFinishedWithinBudget=True firstStopUnfinishedConnections=0 acceptLoopsFinished=True`；`activeHandlers=0 activeRegistry=0 pendingApprovals=0 handlerFaults=0 cleanupFault=False`。
- B完整结算`UNMET / exitCode=2 / aborted=False / background=无故障`并有`RUN COMPLETE`；A上一条已完整结算`FAIL / exitCode=1 / aborted=False / background=无故障`。本次补齐有效自然结束和清理证据，**错密钥专项PASS**，保留原A FAIL/B UNMET标签，不重写日志。
- A输入遮挡人工通过；实际日志已输出通用远端拒绝提示。双端错key与pin仍仅顺序关联；两个超时精确配对，局限不因补尾部而消失。原`UNOBSERVABLE`字段保留，不推断close_notify或TLS阶段拒绝计数。

**后续**：不重跑错key、正常授权、降权、拒绝、B密钥六项或A输入遮挡。剩余窗口生命周期和真正serverProof错误显示按原清单补证。下一小项为B无客户端连接的监听中关窗：观察窗口正常退出并取该独立新run的持久化日志；主动关窗预期INVALID_RUN，不能作为成功认证轮，但可按取消/清理专项核对。该无会话路径不替代活动已认证会话停止。真实serverProof故障需要受控测试对端，不能让用户继续输错key代替。M4仍进行中，不进入M5。

本次仍仅记录用户实测，无代码/网络变更、无build/test或重打包。交付核对摘要：`outputs/M4-错密钥核对-20260928.txt`。

### 18.20 B监听中关窗：取消/清理及窗口自行退出均通过（2026-09-28）

**来源与范围**：用户在A自动化受限后自行改用B测试，先提供gui.log渲染行，随后补交独立Host日志。runId=`1c41d46e`，startedUtc=`2026-09-28 02:52:00.021Z`，PID=`17360`，路径为B的`m3-host-20260928-025200-1c41d46e.log`；不是助手操控A的结果。两个组件哈希仍匹配§18.14审批修正版，非完整安装目录校验。

- 已记录`[HOST] 等待连接 180 秒`，随后明确`操作员中止，来源：关闭窗口`，证明不是只打开了空窗口就关掉，而是监听启动后触发窗口关闭路径。
- 两条中止来源分别为“关闭窗口”和“被控端停止监听或初始化取消”，是同一run在UI与Host取消收尾路径的记录，不是两轮运行或仅凭重复行认定两次人工操作。
- `authenticatedActiveBeforeStop=0`；`sessionHandled=0 active=0 sum=0 partitionOk=True`。没有已认证会话；handler计数不覆盖同子网/准入/TLS拒绝，相关UNOBSERVABLE仍保留，不外推为全部网络活动为0。
- `registrySamples=63 sampleSpanMs=6202.419 maxSampleGapMs=110.985`。约6.2秒是采样覆盖跨度，不是点击关闭后的退出耗时，也不是完整180秒自然结束。
- `firstStopAllFinishedWithinBudget=True firstStopUnfinishedConnections=0 acceptLoopsFinished=True`；`activeHandlers=0 activeRegistry=0 pendingApprovals=0 handlerFaults=0 cleanupFault=False`。`trackingDropped=0 hostForcedClose=0 registryViolations=0`，取消后的后台清理符合要求。
- 最终`INVALID_RUN / aborted=True / exitCode=4 / background=无故障`，有`RUN COMPLETE`。保留作废标记；这次专项故意触发关窗取消，可核对取消和清理，不作为认证成功、保持成功或自然释放证明，也不改变此前其他run结论。

**判定**：关闭窗口触发取消及Host后台清理证据通过。`RUN COMPLETE`只证明角色结算，`exitCode=4`是验收结果码，不能由此证明GUI进程已退出或其操作系统退出码；用户还未单独确认窗口是否自行消失且未强制结束。因此只补这一句观察，无需新日志或重跑。A重复执行子任务被B已执行路径替代，A未实测、工具执行限制未解除；不把解除限制作为此路径重复验收门槛。

**后续确认**：用户在被明确询问“窗口是否自行消失且没有强制结束”后回复“是自行消失了”，并要求继续开发。结合上述同轮日志，无已认证会话的监听中关窗专项完整通过。未实测退出耗时或OS进程退出码，不扩展证据范围。

**仍余**：活动已认证会话停止、真正serverProof失败固定提示及原清单未覆盖项；M4进行中，不进入M5。一般拒绝或错key不能替代serverProof错误显示。该关窗证据核对阶段只核对用户日志与记账，无代码/网络改动，无build/test、未重打包；当时Last code commit为`67c7d7e`。随后用户要求继续开发，新增跨层回归见下节。

### 18.21 M4真实TLS到离屏WPF跨层回归（2026-09-28）

**实现提交**：`608f5c6`，4个源码/测试文件。`ClientRole.RunSuccessAsync`改为internal并接收`DeviceIdentity`，原生产入口仍传`context.Identity`；`WriteOutcome`仅调整internal可见性。没有替换连接器、放开CLI密钥入口、改变协议/审批/超时/网络逻辑，也未改产品App。无需读取用户vault。

**新增7项**：
- `AcceptanceTlsIntegrationTests.cs` +4：真实TLS高层认证、真实Inbox批准降为ViewOnly、真实registry活动，再触发离屏WPF停止按钮或`Close→OnClosing`。客户端一直持有至Host停完；断言首次停止完成、强关桶、资源归零、唯一INVALID_RUN结算及UI任务/CTS/日志订阅归还。两项另用有界取消回调闸门证明角色已完成但CancelAsync未归还时不能提前Reap。
- `MainWindowAuthenticationTlsTests.cs` +3：自有密钥/证书的独立UTF8/HMAC对端，验证正确clientProof后将serverProof翻转一位；有/无pending各一次，经过真实ClientRole分类/WriteOutcome/RunState/worker/Reap/FinishRole到真实ResultBannerText，固定文案完整且没有通用拒绝替代。第三项发送正确proof，在已验证会话的持有段点击客户端中止，验证对端关闭、未伪造五秒保持完成和PASS。对端关闭必须先于本地Host Stop；RST读取计数保留未知，不以默认0冒充测量。
- 增强`MainWindowApprovalUiTests.cs`已有2个布局用例：实际应用样式、800×640保守客户区预算下，固定错误文案横幅及底部日志按钮不越界；未新增DPI矩阵或全部控件同屏门槛。

**本机实测**：定向11/11（含原4项TLS用例）；最终Debug/Release分别：Core125 + Protocol299 + Integration3 + Security66 + Transport633 + Acceptance210 = **1336 PASS / 0 FAIL / 0 SKIP**；两配置构建均0警告/0错误。首次构建的1条xUnit2029分析器警告已修正为DoesNotContain，最终构建不带该警告。

**变异验证**：临时去掉错误文案缓存→两项真实proof错误测试2/2红（期望文案、实际null）；临时去掉Reap的取消任务完成门禁→两项闸门测试2/2红（提前Finish触发取消回调收尾故障，最终由后台故障断言捕获，不声称精确命中某一行所有权断言）。两处均恢复并用git diff确认无残留，再做上述双配置全绿。原始TRX及4份构建/测试日志在`outputs/m4-cross-layer/`。

**覆盖边界**：本次为本机真实回环TLS+离屏STA/WPF，不Show、不创建桌面原生窗口、不调用OnLoaded，不访问真实身份/密钥，不修改现网。Host测试复用真实HostCounters/TransportHost但不是整个HostRole.RunAsync装配；Client复用真实success核心但不是发现及RunAllAsync四场景编排。不得以此宣称剩余人工实屏观察通过、整个M4完成或M5已开始。既有人工通过项继续有效、不重跑。

**当时停点（现已续作至§18.22）**：代码层跨层缺口已补；原清单仍余活动认证会话实屏停止、真实serverProof固定提示实屏表现和未覆盖布局观察。A桌面自动化执行权限仍未解决，不通过替代通道绕过。该轮未fresh publish、未覆盖安装目录。交付`outputs/M4-跨层回归核对-20260928.txt`。

### 18.22 本机隔离实屏入口与截止分类修复（2026-09-28）

**实现提交**：`2596357`，20个文件，+2856/-108。不是仅更新计划：代码、实际测试与fresh发布均完成；M4剩余实屏仍未完成。

**入口与隔离**：

- `LanRemote.IsolatedAcceptance` 新增薄WinExe/WPF项目，直接构造真实Acceptance.App(isolatedOnly:true)，无参数仅进入隔离模式。普通Acceptance无参数行为保留；专用入口拒绝headless/混合参数，模式在Loaded前确定。无启动脚本、无外部进程代理、无自动批准。
- `IsolatedUiScenario` 使用每轮随机身份、16字节key、证书、IPv4精确127.0.0.1随机端口；不构造AcceptanceContext、不读取用户vault/配置或启动发现。独立临时日志；普通自检/Host/Client/查看密钥在隔离模式均有方法级拒绝。
- 复用真实TransportHost/ControlClientConnector、同一Inbox及MainWindow worker/CancelAsync/Reap/FinishRole链。活动专项认证后最多120秒，普通success仍5秒；Host活动快照与首次Stop在客户端取消前。停止仍INVALID_RUN、无操作到期UNMET。
- proof专项校验正确clientProof，经手动审批后按实际grant计算serverProof并翻一位；真实客户端拒绝码必须为client-server-proof-mismatch，固定文案经原日志/结果链显示。故意负例保持FAIL，不翻绿；审批自然60秒到期UNMET，无注入。EOF/RST计数分开，关闭须在HostStop前观测。完成handler引用及时移除，避免累计无界历史列表。

**全量揭示并修复的旧竞态**：

1. 首次全量原服务端用例期待auth-approval-timeout、实际auth-approval-unavailable。旧AuthenticationDeadline无条件在timer回调取消，而单调截止可能未到；受控时钟599.5/600ms提前触发，在真实TLS测试确定性复现。修复回调复核单调剩余量，必要时重排；不足1ms只调整通知排期，不增加接受宽限。覆盖构造同步回调、Dispose重排竞态、父取消和晚回调。
2. 下一全量原客户端用例期待client-approval-timeout、实际client-frame-timeout。客户端已有deadline，但FrameReader再启独立系统CancelAfter；受控流399.5/400ms取消实际重复CTS，确定性复现。增加内部仅消费deadline令牌的读帧入口；公共FrameReader接口保留，缓冲清零共用原实现。失败也复核父取消>绝对窗口到期>真实帧错误。短帧deadline仍报frame-timeout，不把两码都接受为通过。
3. 原现场未采集timer/单调时间戳，不能将“Windows截断恰好导致该次提前量”写成现场实测；确定性故障机制与现场同错误码已验证。旧测试未放宽。初始测试另修缺using、OnClosing反射歧义、Loaded后WPF可能排队ContentRendered导致的错误空日志断言。

**最终实测**：

- Debug与Release各：Core125 + Protocol299 + Integration3 + Security66 + Transport658 + Acceptance282 = **1433 PASS / 0 FAIL / 0 SKIP**，构建0警告/0错误。
- 相对1336基线新增97项：Acceptance+72（引擎/离屏UI/专用入口路由），Transport+25（deadline及读帧边界）。Python打包回归另计14/14，不混入.NET总数。
- 独立启动器不在sln中，另行Debug build 0警告/0错误，Release win-x64自包含fresh publish成功；两配置全量覆盖引用的Acceptance与Transport。未启动该新包的实际桌面窗口，不冒称实屏已通过。
- 原始证据`outputs/m4-isolated-ui/`：`build-{debug,release}-validated.log`、`test-{debug,release}-validated.log`、12份`validated-*.trx`、`launcher-build-debug.log`、`package-tests.log`、`publish.log`。所有失败中间轮保留。代理补做定向复现/修复验证后，最终结论以主线程上述全量为准。

**新交付**：`outputs/m4-isolated-ui/LanRemote-0.1.0-m2-m4-isolated-ui-win-x64.zip`，63729201 bytes（60.8MiB），424个唯一成员，CRC完整，含正常多语言子目录（不是扁平包）。主入口`LanRemote.IsolatedAcceptance.exe`为x64、PE subsystem=2；无普通Acceptance.exe、无ps1/cmd/bat/lnk，另含.NET运行时createdump.exe（非用户入口）。ZIP SHA-256：`c9dcbc04fbb201d1870915c65bd218f1599868eefbcfaeb459e845bee8e8fb5b`。`START-HERE.txt`与交付说明逐字节一致。

fresh源目录`artifacts/m4-isolated-ui-73x0r3z0`；包构建于代码提交前，和2596357相同源码，但不冒称程序集版本已嵌入该提交。旧A/B安装目录与旧包未覆盖。曾尝试最小相对lnk，原生GetPath为空、未通过验证，实验已撤回，改用实际WinExe入口，不交付不可靠快捷方式。

**该轮交付停点（后续现场见§18.23）**：解压新包、双击专用exe，在A本机确认固定错误文案和活动停止后恢复操作，同时查看原范围布局/日志入口；不需B、不改网、不新增DPI矩阵、不重复已通过项。界面自动化权限仍未解除；不绕过拦截，离屏测试不代替实屏。收到实屏结果/日志后继续M4核对，完整收口前不进入M5。

### 18.23 A本机隔离专项现场日志核对（2026-09-28，待剩余停止及实屏确认）

- 用户启动新隔离包，A=DESKTOP-D132BMD、PID61476，gui.log记录窗口1000×940；这证明渲染事件，不证明全部控件可见。读取用户给出的两个完整本机run日志及gui.log，不仅依赖粘贴文本；日志目录为`%TEMP%\LanRemote-Isolated\5673874ef085480d88e374a70e21fe8f\`。安装在`C:\DIYTools\LanRemote-0.1.0-m2-m4-isolated-ui-win-x64`的启动exe、启动器dll、Acceptance.dll、Transport.dll四个文件SHA-256均与§18.22交付ZIP对应成员完全一致；日志所记exe/Transport的SHA也匹配。exe version=(unknown)不作为版本相同的依据。
- **b42a1553：错误proof拒绝及清理的日志判据通过。** 同一requestId/generation/sessionId/connectionId的手动Approved/Control之后，`flippedBits=1 registry=NOT_APPLICABLE`；真实客户端拒绝`client-server-proof-mismatch`，固定文案已进入RESULT.detail；故障对端先观测`kind=eof bytesAfterReply=0 beforeHostStop=True`，随后首次HostStop。首次停机预算内完成，handlers/client均join，registry/审批/连接相关计数全0，keyCleared/certificateDisposed=True，cleanupFault=False、background无故障、aborted=False、RUN COMPLETE。保留原FAIL及exitCode=1（通用“不符合预期”标签），不将日志改绿；registry=NOT_APPLICABLE不冒称真实Host注册认证会话。用户尚未明确确认实屏完整看见该文案。
- **e339ed58：最终UNMET，未执行人工活动停机；清理正常。** 初读文件仅至第38行，后续读取已见完整结局（第56行RUN COMPLETE）。真实客户端`serverProof=verified`、grant=Control，SessionId与HOST AUTH-BEGIN一致（`b28e4672-6c8f-4923-9dc3-7cdd4d2bd47a`），证书pin匹配；随后`localObjectHeldMs=120000`、同步Dispose、clientOutcome=PASS。最终RESULT.detail明确“本地保持期限已到，未执行人工活动停机；本场景仍为UNMET”，aborted=False、background无故障。Host首次停机前活动快照=1，但clientTaskCompleted=True；这是客户端保持到期后Host收尾，与人工活动停止不同。Host记录1200个会话样本/119902.881ms跨度、hostForcedClose=True、naturalRelease=UNMET；首次停机预算内完成，所有registry/handler/审批/连接归零、双侧join、cleanupFault=False。不能用客户端PASS或最终清理全0判人工停止通过，也不将UNMET当作产品认证失败。只需重新执行活动专项并在已双向认证时点停止，不重跑proof。
- 本轮未修改源码、网络或运行窗口，未重新build/test；1433/14仍是§18.22代码验证记录。该轮余项随后续作至§18.24；历史UNMET不改判。

### 18.24 A本机人工活动停止日志通过（2026-09-29本地，实屏反馈待确认）

- 直接读取完整`m3-host-20260928-160938-0cd5d3b8.log`和同目录gui.log；目录为`%TEMP%\LanRemote-Isolated\163570753cab4e318ee5c0af079975da\`。A机器、PID34200；本次日志exe/Transport SHA与§18.23已比对交付包的值相同。开始时间UTC 2026-09-28 16:09:38.435对应本地9月29日；gui.log仍仅证明1000×940渲染事件，不代替实屏反馈。
- **活动认证停止的日志判据通过。** 手动Approved/Control，客户端serverProof=verified且pin匹配；全链SessionId=`98ab2a85-3c8b-4e0e-b9ed-0abade990fb6`、connectionId=`9f09ff3b-262e-46b5-a7cb-2d50ed2f86d6`一致。中止来源明确为“UI 停止本机专项”，首次HostStop前`authenticatedActiveBeforeStop=1`且`clientTaskCompleted=False`，不是120秒自然到期后再停Host。Host终态`authenticated-host-forced-close`、rejection=host-stop，随后客户端join完成。
- Host记录621个会话样本、观察跨度61998.404ms、最大间隔111.221ms；这些是停止前在线采样，不是清理耗时。首次Stop预算内完成、未完连接0、acceptLoopsFinished=True；handlers/registry/审批/连接全0，handlersJoined/clientJoined/keyCleared/certificateDisposed=True，handlerFaults=0、cleanupFault=False，background无故障，RUN COMPLETE。
- 原始`INVALID_RUN/aborted=True/exitCode=4`及`naturalRelease=UNMET/evidence=UNMET`全部保留：前者阻止把人工中止当成普通认证整轮PASS，后者明确本轮不是自然释放。专项目标正是人工停止，依据上述来源、停前快照、首次停机和清理事实判定，不修改原日志或将整轮翻绿。
- 与b42a1553合并后，两项专项日志证据均齐，不再要求重跑proof/活动停止或旧两机项。只余用户对原实屏事实的反馈：固定错误文案是否完整可见、停止后窗口是否恢复可操作、审批/停止/结果/日志入口是否可访问；不加截图或DPI门槛。未改源码/网络，未重跑build/test，M4完整收口仍待该反馈，不进入M5。

### 18.25 M4 最终收口（2026-09-29，明确实屏确认已收到）

用户本轮原话：**“上一轮询问的实屏情况都正常。”** 按交接prompt第29行既定语义，确认b42a1553结果区域固定文案完整可见，以及0cd5d3b8停止后窗口恢复可操作、审批/停止/结果/日志入口正常可访问。确认来自用户实际观察，不由渲染日志推断，也不是把交接请求视作确认。

| 原范围核对项 | 结果与已有证据 |
| --- | --- |
| 9项实现及8项规格测试 | 阶段0–5、§18.5–11/§18.21–22；最后代码2596357，Debug/Release各1433 PASS，构建0警告/0错误；Python打包14/14另计 |
| DoD：绝不发送raw access key | §18.6双档transcript/HMAC、§18.7严格DTO及§18.10高层客户端；线上仅nonce/proof/批准后的随机sessionToken，长期密钥不在认证帧；秘密副本清理/不入日志已有测试，不承诺DTO字符串或TLS内部副本擦除 |
| DoD：auth success后才能有session | §18.8成功帧写出后才注册；§18.10客户端验证实际pin和serverProof/grant后才交出会话；失败/取消/到期不注册；真实成功、错key、拒绝和坏proof证据配齐 |
| Control、降权、拒绝、错key | §18.15–19原有效轮通过；坏proof的FAIL和负例UNMET保持原样 |
| 网络无影响、审批与短码、密钥显示/输入遮挡 | §18.15–19原人工确认有效，不外推每轮连续网络遥测或所有DPI |
| 监听中关窗 | §18.20 host1c41d46e清理与用户“是自行消失了”已配齐 |
| 真实坏proof固定文案 | §18.23 b42a1553拒绝/EOF/首次预算内清理与本轮实屏确认配齐 |
| 已认证活动停止及恢复操作 | §18.24 0cd5d3b8停前活动1/client未完成、先Host后client、首次预算内归零，与本轮实屏确认配齐 |

**结论：M4及原21步完整完成，可以顺序推进M5。** 原日志FAIL/INVALID_RUN/UNMET均不改判；e339ed58历史到期轮不再阻断。未重跑已通过专项、未要求截图/DPI矩阵、未改网络或防护、未操控桌面、未重打旧包。v1总清单中的VideoAttach、视频持续运行、输入、多会话等属于后续里程碑，未提前标通过。

**Last code commit：2596357。Working tree at validation：收口前main工作树干净且与origin/main一致，源码/测试/打包文件与§18.22已验证基线一致；本次M4收口仅文档/记忆记账，没有新build/test。** 1433和Python14明确为既有实测，包SHA及四组件归属仍以§18.22–23为准。跨会话旧任务#52/#3不存在于本轮任务列表，不臆造旧任务状态。

## 19. M5 视频最小闭环（2026-09-29，进行中）

M4收口记账提交`c251d21`后推进；本节不覆盖M4原日志结局。**Last code commit：`1c710a9`。Working tree at validation：该提交对应的11个源码/测试文件与ADR-047工作树；最终solution双配置验证后未改代码，提交后再次核对src/tests/tools/scripts无差异。**

### 19.1 首片：帧所有权、DropOldest和二进制framing（完成）

- Core `BoundedFrameQueue<T>`：容量1/2、默认2，有界排队、丢旧释放、拒绝写仍归调用方、读取移交消费者；取消不偷帧，Stop拒绝/唤醒/清空且不会误释放消费者持有的帧。释放失败可观测，先尝试所有存量帧再聚合错误。容量不包括在途帧/同步释放中的旧帧，Stop不是整个管线join。
- Core `EncodedFrame`：保留旧构造，新增显式有效payloadLength，防止池多租尾部被当作JPEG发送；构造失败不转移owner、有效payload可小于池容量。
- Transport `VideoFrameHeader/Reader/Writer`：internal 40字节LRVF v1、BE字段、无额外Control前缀，合法头后才租内存；无效字段、超限、截断、取消或重叠调用永久终止，不重试半帧。Writer不拥有调用方帧；Reader成功交付后消费者拥有。v1 flags/reserved、质量与时间戳范围定案ADR-047，不伪称原规格已有全部细则。
- 未改变现有认证/hello/SessionRegistry或公开流/token访问面，VideoAttach仍未放行。没有真正JPEG编解码/采集/实屏/视频TLS接线，不将合成payload称为JPEG验证。

### 19.2 审查修复与验证

首次审查发现：Reader构造EncodedFrame时的第二次owner.Memory getter能先取消/停止再返回，原实现缺交付终检；owner.Dispose会覆盖主错误；Stream.Dispose失败被吞且不能观测。新增同组20项先红再修再绿，测试文件SHA未变；其中诊断接口缺失类红测不表述为“20项均复现资源泄漏”。修复为未提交帧+最终取消检查+CAS交付提交点、唯一失败清理责任、主异常原实例/类型/令牌保持、有界清理诊断（Reader最多2/Writer最多1）。只读复核解除三项阻断，未放宽断言。

**主线程最终实际执行（SDK 10.0.401）：**

| 项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 196 | 196 |
| Protocol | 299 | 299 |
| Integration | 3 | 3 |
| Security | 66 | 66 |
| Transport | 893 | 893 |
| Acceptance | 282 | 282 |
| **合计** | **1739** | **1739** |

- 两配置构建均0警告/0错误，测试0失败/0跳过；12份TRX用独立XML汇总核对Counters、实际结果数量及全部Passed。相对M4的1433净增306：Core+71（队列42、帧模型净增29）、Transport+235（含审查20）；重复定向运行与Python不混入总数。
- 不在solution内的 `LanRemote.IsolatedAcceptance` 另行Debug/Release build成功，均0警告/0错误；只验证引用兼容，不启动桌面、不重跑M4操作、不publish或覆盖已核验ZIP。
- Python标准库`struct`独立复核40字节黄金向量及各偏移；未调用生产Writer作为期望值。队列并发/取消专项曾另行5轮每轮6 PASS，仅补充证据。
- 证据目录 `outputs/m5-foundation/`：`solution-build-{debug,release}-validated.log`、`solution-test-{debug,release}-validated.log`、12份`validated-*.trx`、`launcher-build-*-validated.log`；红绿与同断言SHA见`video-review-summary.log`及`video-review-*.trx`，黄金向量见`video-review-python-golden.log`。初始失败日志保留，outputs不自动入Git，推送不等于上传本地证据。

### 19.3 后续顺序与仍未完成项

1. 已完成：原有capture/encoder抽象与帧模型盘点、有效内存所有权、有界队列、二进制header/parser/reader/writer。
2. VideoAttach安全基础片已完成见§19.4：83字节transcript、16字节nonce、写前15秒绝对窗口、原子消费/撤销；旧registry token getter已删除。下一片接第二条TLS/pinning/同子网准入，超时清表的停机跟踪前置缺陷已修见§19.5；ACK/等待/生命周期实施细节另记ADR，不把只读建议当已冻结协议。
3. 随后：GDI主屏采集、50%缩放、JPEG编码与解码/WPF显示，以5 FPS先做最小闭环；现有Control保持读不能新增并行读者，需明确会话交接。无必要不新增Video项目或改TFM。
4. 最后验证M5 DoD：完整链路10分钟无明显持续内存增长、慢网丢旧且延迟不无限累加、断连join和资源释放。仅队列测试不代替这两条；目前均未运行。

**当前无须用户重复M4人工操作；M5尚未完成。** 不改系统网络/防护、不回写原始规格，不把桌面权限限制当成可绕过的口头授权。下一步继续自主实施可自动验证部分，只有真正人工/权限依赖才提出。

### 19.4 VideoAttach安全基础片（完成，8e1785a）

**Last code commit：8e1785a。Working tree at validation：该提交对应的源码/测试树；最终双配置全方案验证后仅文档记账。** ADR-048为实施细则，不回写原规格。17文件/+2760/-87；不是第二TLS或完整M5交付。

- internal `VideoAttachProof`固定83字节：域分隔19、UUID RFC/network-order16、nonce16、实际视频TLS证书摘要32；HMAC token/proof均32。Python标准库uuid/hmac独立三组向量，未用生产helper作为期望值。
- internal `VideoHelloFrame`严格六字段、规范UUID/Base64、4KiB；旧`HelloFrame`和默认public pre-auth依然只收Control，纯解析器可用不等于视频网络入口已放行。
- `VideoAttachWindow`在success序列化后/首次Write前起算，min(正值配置,15000ms)，write/flush/调度/登记全计入，elapsed>=budget或负值拒绝；登记不续期，已成功视频不因窗口到点撤销。
- `SessionRegistry.TryAttachVideo`删除旧token getter，在同锁内校验冻结身份（不同连接ID、同远端IPv4、同pin）、MAC和最终取消/截止/entry对象身份，再一次消费；错误proof不消费，正确并发仅一赢家。注销按对象身份防ABA，同锁摘表/清零/撤销；lease不拥有流、不恢复资格，后继必须锁外关闭并join。客户端旧internal token视图仍留待客户端片改造。
- success写后才登记的M4门禁保持；访问key生成serverProof后立即清理。审查抓到catch内await FailAsync延迟三份原秘密清理，新增真实状态机原数组观察4红/1绿，修为先finally清零再失败帧I/O，原测试SHA不变，Debug/Release各5绿。DTO字符串及TLS内部副本不承诺擦除。
- 注册冲突时未转移的新token副本有finally清零；目前测试只直接观测调用方/原entry，未持有该失败新副本，保留漏测声明，不误写成现有泄漏。复核已解除上述清理阻断，无本片新阻断。

**最终本机实际执行（SDK10.0.401）：**

| 项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 196 | 196 |
| Protocol | 299 | 299 |
| Integration | 3 | 3 |
| Security | 66 | 66 |
| Transport | 1102 | 1102 |
| Acceptance | 282 | 282 |
| **合计** | **1948** | **1948** |

- solution双配置build、test均成功，0警告/0错误/0失败/0跳过；独立IsolatedAcceptance双build同样通过。12份TRX独立核对Counters与每条Passed，较首片1739净增209，不能把重复专项相加。
- 三项真实源码变异：移除MAC后检查2红、不消费资格2红、写后才起窗口4红；全为运行期断言失败。逐项精确恢复，源码SHA一致；恢复后同组8绿并重建。详见`outputs/m5-video-attach/mutation-summary.txt`及4组TRX，原红结果保留。
- 同目录`solution-*-validated.log`、`launcher-build-*-validated.log`和12份`validated-*.trx`为最终证据；`cleanup-review-*`为清理红绿，`protocol-golden.log`为独立向量。最初后台attempt未产生日志，不计验证。汇总脚本首次在构建日志打印部分有转义错误；TRX部分已核对，四份构建日志随后直接读取确认。
- 真实Control TLS测试中视频安全上下文为合成对象，受控I/O窗口测试也不是第二TLS。未publish/打包、无真实视频/采集/JPEG/实屏或十分钟DoD。旧M4包/安装目录不动，outputs不上传。

**后续**：第二TLS Router/ACK/有界登记间隙及客户端生命周期仍待实施；规划时发现`ConnectionRegistry.StopAllAsync`超时后清空未完成entry，重复Stop可能误报0，先补红测修复该前置条件，再接线。不把规划中的100ms/40ms或ACK字段当成已经定案或实现。

### 19.5 第二TLS前置：停机超时跟踪修复（完成，546d02a）

**Last code commit：546d02a。Working tree at validation：本提交源码/测试完整工作树，最终全方案验证后未改代码。**

**范围**：仅ConnectionRegistry与对应测试，ADR-049。接线前自主修复真实发现，不重跑M4现场/人工项，不改网络、默认装配或认证安全策略。

1. Stop超时不再清空未完成entry/释放其CTS；重复Stop继续返回真实未完成数，尚在运行的registration仍可取得原取消令牌。
2. 每entry的强制释放在锁内检查/认领、锁外执行；并发/重复Stop登记表至多尝试一次，合作handler已经完成的旧snapshot跳过。force-inflight期间即使租约已完成也保留跟踪。
3. CTS回收与根取消使用互斥认领：根取消只发起一次，取消期间完成的handler不立即同步解绑linked CTS，由原取消finally回收；避免取消回调等handler而handler等父取消回调。普通完成回收期间仍留表，首Stop先等其解绑；无新增清理后台线程。
4. 审查修复先发布租约完成TCS，再允许CTS清理/摘表；旧快照不会仍缺完成通知而新Stop已经得到空表。内部TCS均异步continuation。

**已执行的红绿证据**：
- 新增4项：重复Stop保留Count、超时后令牌可用、并发Stop强制释放一次、合作handler资源不再释放；旧实现4/4运行期FAIL，第一版修复后登记表Debug/Release各11 PASS。首批测试文件SHA=e9a0d56d44f22a22f55655139b9334041f0860aae694948bcd3e0b009cfad89b。
- 审查新增2项：完成先于摘表1红、force未返回不提前报告完成1绿；前者直接调用内部Remove接缝并持有真实完成TCS，非概率调度或真实TLS竞态声称。修复仅将完成发布前移，测试文件SHA=64f48262460ab72a9ee5acc1692e8523be8df340b62d934907767a3f3b2c7716，最终验证后相同，未放宽断言。
- 原始红测分别`outputs/m5-stop-tracking/red-debug.*`和`review-red-debug.*`，第一版绿测`green-{debug,release}.*`，不得将11当最终新用例总数。

**最终实际验证**：solution Debug/Release各**1954 PASS / 0 FAIL / 0 SKIP**（Core196/Protocol299/Integration3/Security66/Transport1108/Acceptance282），其中登记表当前13项；12份TRX逐项Passed和Counters独立核对。solution与独立IsolatedAcceptance的双配置build均0警告/0错误。证据`outputs/m5-stop-tracking/solution-*-validated.log`、`launcher-build-*-validated.log`及12份`validated-*.trx`。验证后源码/测试未再改；最后只读复核解除完成窗口阻断。较§19.4净增6项，不把重复专项或首次未完的5份TRX算一次全量。

**未扩大保证**：本片不修复任意同步取消/Dispose/CTS解绑阻塞或所有清理异常聚合；不承诺Stop能硬中断本机任意实现。登记表至多一次force不代表handler自行Dispose与force之间全局exactly-once。第二TLS接线还须实现Host绑定的窄关闭授权、清理异常可见、跟踪取消回调和完整join，不能用本片留表替代；Router/ACK/客户端安全附着仍未实现。

### 19.6 服务端双TLS路由与生命周期片（完成，5a03ab1）

**Last code commit：5a03ab1。Working tree at validation：本提交对应源码/测试，最终frozen证据后未改源码/测试。** 26文件，新增5847/删除69行（含ADR-050），主要是确定性与真实TLS测试。不等于完成M5；未改产品默认装配、App、系统网络、防火墙、旧M4包或安装目录。

**实际实现**：
- internal单次FirstFrameRouter仅消费一次严格首帧；Control直接交既有认证，Video使用独立TLS和ADR-048资格。原public pre-auth仍拒绝完整合法video hello。Host提供显式CreateWithChannelRouter，集成测试使用显式handler抓取诊断，未直接覆盖factory本身。
- 仅初始NotRegistered最多100ms/3次/40ms间隔重试；最终entry失效或ABA为Unavailable不重试。ACK严格四字段video_attach_ack，4字节BE前缀；ACK write+flush及检查完成前不取帧，之后直接LRVF+有效payload。ACK默认2秒、整帧写默认5秒；保持期不受首帧信封或15秒附着窗口误取消。已消费资格失败后不恢复。
- Host绑定原Security/Stream的关闭能力，public两参/替换引用副本无授权。共享close任务分别尝试socket/SSL，回收迟到SSL；adapter真实转发，Dispose请求同一close。Host按关闭尝试→准入→登记收尾，七类首错有界保留；未登记拒绝由accept持有，已登记并发由registry持有。
- source串行返回拥有型帧，sender等write结束再释放，迟到帧也回收；视频持有transfer/唯一上行read/CancelAsync/close全部join。Control复用原审批read/decision并join，不新增reader。不合作源会持续出现在重复Stop未完成报告中，不强行翻绿。

**修复与证据链（均未弱化断言）**：
1. 继承首次定向175 PASS实际取回；新增Router边界35 PASS。独立审查发现来源IO/ODE/OCE被误吞、finally覆盖主体、WhenAll await丢多故障；同组11项运行期全红，修复保存来源首错、主体与收尾统一聚合及完整原任务异常后，全套221 PASS。
2. 真实双TLS中source未取消先抛OCE，取消回调gate保持Router收尾，Host随后取消：新增1项红（Host诊断为空）。Host取消豁免收窄至异常token==registration token后，相关83 PASS。
3. 收窄后子deadline的正常取消误报，prefix/payload/NotRegistered等待三项先3红/35绿；Router仅在首帧/附着受控边界归一caller token，视频来源保持独立。最终七类定向Debug/Release各225 PASS。最后只读复核无本片可证实剩余阻断，不外推任意handler伪造同token的取消分类。
4. Host未登记关闭与Control迟到decision等待此前为静态证实后修复，并在本轮组件测试通过；没有声称这两项已有独立红测。未登记Stop门槛是组件接缝测试，不冒称真实网络拒绝端到端复现。

**最终验证**：solution Debug/Release各 **2196 PASS / 0 FAIL / 0 SKIP**，分别Core196、Protocol299、Integration3、Security66、Transport1350、Acceptance282。共12份`frozen-*.trx`按XML计数及每个UnitTestResult=Passed独立核对，solution和IsolatedAcceptance双配置4次build均0警告/0错误；`git diff --check`通过。原始证据均在`outputs/m5-dual-tls/`，不上传原始日志：
- 修复前：`router-regression-before-fix-debug.*`（11红/35绿）、`host-oce-before-fix-debug.*`（1红/12绿）、`stage-cancel-before-fix-debug.*`（3红/35绿）。
- 最终定向：`final-targeted-{debug,release}.*`；最终全量：`frozen-test-{debug,release}.log`和对应TRX；构建：`frozen-build-*`/`frozen-isolated-build-*`。
- `initial-*`、`router-regression-after-*`、`host-oce-after-*`、`final-test-*`、`validated-*`是中间工作树证据，不冒称最终树。未重新运行Python打包或M4实屏。

**真实TLS覆盖边界**：13项，使用生产Control/TLS客户端、实际证书pin验证、独立HMAC/ACK/wire断言；覆盖成功、错proof不消费、重复附着、Control撤销、视频断连、Host停机、忽略取消源、回调故障、非法上行、旧Control-only拒绝及跨信封/附着TTL仍可发后续帧。测试payload为不透明五字节，不是JPEG图像。组件测试覆盖严格ACK、首帧/登记/ACK边界与所有权/收尾，不声称全网络组合或硬中断任意本机代码。

**下一片**：客户端高层安全附着，先处理AuthenticatedControlSession共享SessionToken视图与Dispose同步，再实际第二pin/proof、严格ACK验证、唯一交付/迟到成功回收、父子撤销与join；随后GDI主屏50%缩放/JPEG/5FPS/WPF显示及10分钟内存稳定DoD。M5仍进行中，M4不重开。

### 19.7 客户端封闭proof片（完成，7afface）

7afface（7files/+445/-18），ADR-051已记；源码验证后未变。前片5a03ab1/§19.6历史2196保留。

- `AuthenticatedControlSession.CreateVideoAttachProof(nonce,actualPin,caller)`代替共享`SessionToken`；MAC/Dispose同锁、TLS Dispose锁外、失败proof finally清零。
- 测试专用反射保留原秘密数组观察清零及独立TLS oracle；无后置取消直接持有失败proof原数组的确定性观察。并发仅入口锁、不暂停MAC；新方法未高层真实TLS使用。
- 主线程已实际解析12份`outputs/m5-client-proof/full-*.trx`：Debug/Release各**2224 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport1378/Acceptance282）；定向各125含新28，不重复累加。solution/IsolatedAcceptance四build零警告错误；Python独立83字节HMAC黄金已核，非打包Python重跑。
- 本片已改生产与测试；验证之后仅文档记账。无新包/GUI/M4重跑/改网。

**下一步仅已只读定位，未完成：**
1. 先`TlsConnection`共享`CloseAsync`/客户端video stream adapter；现Dispose普通bool判重，SSL抛会跳过client清理；`TlsClientConnector`失败finally同为独立待修缺口。
2. success收齐`receivedAt`/`TimeProvider`元数据（目前丢弃）、本地hint上限15s、父撤销/子登记join、实际第二pin/严格ACK唯一交付及迟到成功回收。
3. 再采集/JPEG/渲染及十分钟DoD；默认Control-only/安全硬约束不变，非全客户端或M5完成。

### 19.8 客户端共享关闭与视频流适配器

本片基于7afface源码，实施ADR-052。TlsConnection新增internal共享CloseAsync/固定CleanupErrors/CreateVideoStream，新增ClientOwnedVideoStream；public接口不扩大。worker先TCP解堵，再独立尝试SSL。首个public Dispose同步等待并报告单错原实例或双错聚合，重复调用等待但不重放；adapter同步Dispose只请求、异步等待同Task，不能把Reader的StreamDisposeSucceeded当物理关闭成功。关闭任务与原I/O任务分别join。

**红绿与范围**：
- `before-fix-debug.*`两项真实运行期红测：SSL错误时TCP释放次数0、双错丢client错误；生产修改后通过。
- 新增39项：关闭组件26、真实回环TLS11、原缺陷2。真实TLS使用产品连接器/严格实际pin，覆盖双向各重载字节、主动关闭后本端和对侧原读取退出。不是视频附着或JPEG/屏幕验收。
- `targeted-debug.log`为测试lambda误选异步重载编译错（并有nullable警告），已显式Action/nullable泛型修正；不计产品红测。`targeted-fixed-debug.*`一项ThreadState二次瞬时读取假失败，改成入口信号+保存一次等待态；未降低Dispose同步等待判据。
- 首次全量`test-debug.log/full-debug_*.trx`抓到旧审批配额夹具并发枚举List；新增先join原clientB再观察原Frames/待批断言，并补两连接/两client任务的finally收尾。原断言/配额/审批预算不放宽；失败优先保原异常，附属cleanup仅在该测试Data记录（报告未必自动显示），无主错则清理错使测试失败。
- 秘密锁断言改为跨线程TryEnter，避免worker上的IsEntered假证；专用线程等待态是入口观测、非内部线性化点。组件close已结束但read仍未完成的反例保留，显式放行再join。非合作同步Dispose无硬截止。

**Last code commit：894f294（8files/+1738/-58，ADR052）。Working tree at validation：该提交源码/测试，最终验证后未变，之后仅文档记账。** 主线程已独立解析`outputs/m5-client-close/verified-Debug_*.trx`与`verified-Release_*.trx`共12份：各**2263 PASS/0失败/0跳过**（196/299/3/66/1417/282），逐条outcome与计数一致；定向各74，solution与IsolatedAcceptance双配置四build均0警告错误。此前`frozen-*`虽各2263通过，但在最后finally改动前，不作为最终树凭证。未打包/GUI/改网/M4/Python打包重跑。

**下一片（仅只读定位，未实装）**：先修TlsClientConnector返回前失败finally（SSL抛会跳过client/覆盖主错）和caller交付终检。主错/清理错需完整保留，正常无清理错保持原异常；如果引入Aggregate，必须同步核对ControlClientConnector取消翻译和Acceptance分类，不能吞清理错或把cross-subnet复合故障判PASS。不要扩成共享LastErrors或放宽TLS/pinning。之后才是success原receivedAt/clock、hint≤15秒、父子撤销/登记/join、严格ACK唯一交付与迟到回收，再采集/JPEG/显示及十分钟DoD。默认Control-only、M4物料和系统网络不动，M5仍未完成。

### 19.9 连接器失败清理与复合错误分类（2026-09-29）

**实现**：ADR-053；TlsClientConnector生产路径单owner持有TCP至交付，不伪造身份复用TlsConnection清理。进入/连接后/交付前caller终检，原connect/auth任务实际await；失败worker按TCP→SSL独立释放、等两项结束。无清理错原主错重抛，有错固定Aggregate(primary, Aggregate(cleanup1[,cleanup2]))，保留实例和嵌套。生产证书validator、TLS选项、冻结目标不变。

Control只对传入复合故障避开caller取消翻译；实例readonly委托不扩大public能力。Acceptance复合树不按TCP/pin/cross-subnet乐观分类：HARNESS_ERROR/UNOBSERVED且脱敏记账，普通取消与复合cleanup并发仍先输出场景错误，整轮操作员停止仍INVALID_RUN。footer回调复合故障记账后原样抛，不重跑Complete；日志已写标记不代表方法成功，完整footer不作无条件保证。

**先红与修正**：
- TLS owner旧语义60例中51运行期红；覆盖connect/create早期TCP-only、同步/异步主错、四种清理组合、原异常/OCE token、释放闸门、迟到成功/失败、并发隔离。
- Control初始80例26红含4项错误预期（最外层OCE async状态本来就是Canceled）；修正测试为保原树且尊重async状态，再临时恢复旧catch得到22红/58通过（`control-corrected-red.*`），后恢复修复。不能将初始4项算生产缺陷。
- Acceptance分类144例96红；收尾独立审查新增断言151例5红（1项取消+复合cleanup场景结果缺失、4项footer重复输出），修后151通过。无放宽原取消/认证/TLS断言。
- 增量251：Transport新增140（owner60/Control80），Acceptance新增111。组件合成identity不是实网证据；真实TLS由原连接器/pin/过期/冻结目标/关闭回归承担。

**Last code commit：6d6a496（7files/+1821/-104）。Working tree at validation：该提交源码/测试，最终验证后未变，之后仅文档记账。** `outputs/m5-connector-cleanup/verified-{Debug,Release}_*.trx`共12份已由主线程XML解析核对所有结果：各**2514 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport1557/Acceptance393）。solution和IsolatedAcceptance双配置四build均0警告/0错误。定向中间Transport241、最终Acceptance151通过；全量覆盖最终同树。无GUI/打包/M4重跑/改网。

**下一片**：Control已取得连接后的认证失败finally仍会覆盖主错，Verify构造session后终检的session.Dispose也可能将超时变成连接关闭。先提取同锁逻辑撤销清零，不在秘密锁/请求线程等网络释放，由外层owner await共享CloseAsync并组合一次诊断；保持public Dispose合同。随后success原收齐时刻/clock/≤15秒TTL、父子撤销join、第二TLS实际pin+ACK唯一交付、采集显示和十分钟DoD。本片不是全客户端生命周期完成，M5继续。

### 19.10 Control 认证 owner 收尾与未交付会话撤销（2026-09-29）

**实现/合同**：ADR-054。AuthenticatedControlSession同锁RevokeCore负责失效与清零，internal void RevokeForOwnerCleanup不触发网络；public Dispose仍锁外同步关闭、重复幂等。Verify构造后的终检失败只撤销，由外层owner等共享CloseAsync。外层catch在慢清理前记录caller快照、清零私有key、撤销session，关闭结束后将原主错与CleanupErrors组合一次；无清理错维持普通取消/复合错误规则。清理期间才发生的取消不追溯改写原失败，成功移交不被finally回收。

**证据与纠正**：
- `before-fix.*`旧owner190例144红；`post-success-red.*`原22例18红。真实Control公开方法消费受控SSL中的严格帧，实际MAC由独立oracle验证，不将mock identity冒称TLS证据。
- 初版构造后测试仅用第N次取时命名且只见parsed token，独立审查发现无法证明真实session撤销。新增internal实例readonly构造观察（public默认null），真实new后事件推进deadline；观察真实session自有token，与parsed token分开。最终测试保存构造时非零原数组，TCP关闭闸前同一数组全零、Stream/proof失效、测试尚未兜底Dispose。
- 两处单点变异：外层撤销删一行10/26红；Verify撤销删一行14/26红，均精确命中自有token未清零/proof仍可用而非编译或超时失败；独立构造/到期事实不随目标删除。变异前源码快照SHA256 `d52b54ca7f377e84690e634f946924d0c780035eb6d44e56a08978afb4987bfa`，主线程核对恢复源码逐字节一致；恢复后26绿。最终同一原数组前后身份断言在此后加强，并纳入最终全量。
- 新增227例：owner190、构造后26、撤销原语11。中间定向415绿；最终全量含全部加强项。key/client transcript/parsed token/session token有原数组观测；expectedProof/grantTranscript仍只有代码finally顺序证据，不宣称所有秘密副本已擦除。

**Last code commit：1bb9063（6files/+2224/-26）。Working tree at validation：该提交源码/测试，最终验证后未变，之后仅文档记账。** 主线程逐条解析`outputs/m5-control-owner/verified-{Debug,Release}_*.trx`共12份：每配置**2741 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport1784/Acceptance393）。solution及IsolatedAcceptance双配置四build均0警告/0错误。未改原真实TLS用例断言、系统网络、M4物料或产品App；未GUI/打包/M4重跑。

**下一片（已只读设计，未实现）**：ReadReplyAsync当前丢弃success原ReceivedAt而返回0；保留原完整payload收齐时刻和同源clock传入session，pending仍保留原严格解析接受时刻，不混用。本地hint封顶15秒、elapsed>=预算拒绝、重复查询不续期，TTL到期不关闭Control。客户端锚点只是本地等待上界，不能冒称准确服务端写前TTL。之后再父子登记/撤销/join、第二TLS实际pin/严格ACK唯一交付与迟到回收、采集/JPEG/显示/十分钟DoD，M5仍进行中。

### 19.11 success 原时刻与本地附着预算（2026-09-30）

**实现**：ADR-055。ReadReplyAsync分别返回SuccessReceivedAt/PendingAcceptedAt；两条success路径均直传原完整payload时刻及window.Clock，pending仍严格解析后接受。session必填原时刻/clock，构造不取时，内部GetRemainingAttachBudget按min(hint,15000)计算；同锁撤销>取消>到期，取时和frequency读取后再查，负elapsed或>=边界抛TimeoutException。查询不续期、无timer/I/O、无资格消费，不改变proof或因到期关闭Control；小hint构造前耗尽仍能交付有效认证Control。本地预算不等于准确服务端剩余TTL。

**证据与修正**：新增47例（预算27/公开入口传播20），原断言未放宽。首个定向112例中2项同步Theory配置Timeout被runner拒绝，去掉该标记后112绿；不将其算生产缺陷。受控SSL走严格帧/独立HMAC，不是新真实TLS证据。最后字节、payload timer释放与真实构造observer独立推进时间，5000ms预算扣400+600+250ms后3750ms；非标准频率/UTC跳变/小大hint/精确边界/负elapsed/重入撤销取消/原token与proof仍有效有断言。未直观测MAC内部耗时。

六轮单点变异分别16/10/6/11/2/2红（各47），依次为success重新取时、pending收齐替接受、去封顶、>=改>、删后置撤销、删后置取消。每轮独立证据`outputs/m5-attach-budget/verified-mutation-*.trx`；pending通过通知脱敏包装产生固定认证异常，内层定位由单点变异与源码断言交叉核对，非TRX直接展示。源码与原快照逐字节恢复后47绿；连接器SHA256 b8dd1f8315ebfa057b636536b643f8f408dd3cf55de6f1eac0788cfd0494a62e，session f29f66adafdc211b8c50f7581d8d46c2a54c9f2cef46dd1da738046111548720。首个执行器裸bash误选WSL未跑测试，改显式Git Bash后执行六轮，不将启动错误算变异命中。

**Last code commit：8cefb34。Working tree at validation：该提交源码/测试，最终验证后未变，之后仅文档记账。** 主线程逐条解析最终十二份`verified-{Debug,Release}_*.trx`，双配置各**2788 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport1831/Acceptance393），solution及IsolatedAcceptance双配置四build均0警告/0错误。未改产品App、系统网络/防护、M4物料；无GUI/打包/M4或Python打包重跑。

**下一片#138（只读设计，未实装）**：先内部父子生命周期/联合交付，不先开public Attach。需要认证公开入口最后提交标记（阻止未交付observer启动子操作）、先登记再启动的固定任务槽、迟到连接/帧owner、同_gate父撤销/子stop/ACK与帧提交、所有已知close独立发起再等取消回调、原任务全部join和有限来源诊断。保持RevokeForOwnerCleanup无网络及重复session Dispose不等待首个慢关闭的原合同。随后接真实第二TLS实际pin/严格ACK及高层唯一交付，再采集/JPEG/显示/十分钟DoD。这里不是已完成父子生命周期或可看屏，M5继续；不重跑M4、不改网。

### 19.12 客户端父子生命周期与联合交付（2026-09-30）

**实现**：ADR-056，#138完成。认证公开入口待所有窗口释放后CommitDelivery，同_gate检查撤销/caller；新attach入口拒绝未交付observer，原proof/预算/Stream与public表面保持。新partial内一个attach和一个read固定槽，先登记再在线程池执行并await原工厂/初始化/读取；同步前缀、迟到连接、未交付frame均有owner。入口及提交预算后复查可重入状态；帧由父/子共同终检交付，已提交帧不追溯回收。

本地尝试登记后永久消费，失败不恢复且Control继续；不改变服务端仅正确proof原子绑定才消费。RevokeForOwnerCleanup零网络/回调，public Dispose首调用独立请求两连接关闭但只等原Control，重复立即返回；internal CloseAndJoinAsync共享任务等原任务/回调/reader/迟到关闭全部退出，固定来源LifetimeErrors保留原多错树，单项只去Task自加容器，不Flatten用户异常。关闭/join完成不等于资源释放成功，不保证硬中断任意代码。

**测试与审查**：新增63例（ClientVideoLifetimeTests57/公开认证最终交付6），定向175 PASS。首轮158例6失败：2项诊断单错包装身份修生产，4项ObjectDisposedException.ObjectName全名修新增预期；3条编译/分析警告以等价断言和显式保存任务消除，旧用例未放宽。

变异独占串行。首轮最终交付变异中2例Guard遮错，停止恢复；第二轮原read join删除仍2绿，抓到只读IsCompleted的调度盲区，同时未交付帧最后兜底Dispose遮主Assert。修复：WhenAny观察原认证Task，最后资源兜底逐项隔离Dispose异常；默认线程池不变，新增实例级joinScheduler测试接缝，严格单任务调度器在其余原任务完成后同步推进join，删除read await必在放读闸之前发现错误完成。旧失败/存活目录保留，不覆盖或冒称已过。

最终证据目录`outputs/m5-client-lifetime/mutation-20260930-140320-57c78ade/`。8轮变异总数4/6/2/2/4/3/2/2，运行期红例4/3/2/2/2/3/2/2；分别覆盖最终认证提交、未交付门禁、入口重入、迟到接管、提交重入、帧联合终检、原read join、多错保真，无Guard/编译错充数。主线程独立解析10份TRX：基线及恢复175/175，8轮均命中。恢复源码逐字节一致：lifetime SHA256 `056d830600e2f30a099d40fe11923d68ceebd2b2f614e865c97d73318cb56f96`，ControlConnector `d2bd6c9b6313caadbd2f4b4ceb740c23cc59b6af02ed59b05e1aef47d2d381bd`。

**Last code commit：eb448d8（6files/+2264/-7）。Working tree at validation：该提交源码/测试，最终验证后未改，之后仅文档记账。** 主线程逐条解析`outputs/m5-client-lifetime/final-{Debug,Release}_*.trx`十二份：每配置**2851 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport1894/Acceptance393）；solution及IsolatedAcceptance双配置四build均0警告/0错误。受控SSL与手工黄金wire不是新真实TLS握手证据；原真实TLS测试随全量执行未放宽。未动产品App、M4物料、系统网络或防护；无GUI、打包、M4或Python打包重跑。

**下一片（仅只读方案，未实施）**：真实高层attach前先补child拥有的执行期原截止timer：从success原锚点计算、不阶段续期，timer回调/释放/取消全部join且保存诊断；当前底座仅检查启动/提交预算，不会主动超时打断不返回的工厂。敏感hello序列化和wire副本须在原I/O结束后清零；复用原生命周期，以Control冻结目标建立第二TLS，用actual第二pin计算封闭proof，ACK严格解析并比对父SessionId，精确只读一帧后交付唯一reader。避免在worker内等包含自己的join、避免重复入口失败停止别人的attempt。Control唯一reader/远端Control关闭主动感知、采集/JPEG/显示/十分钟DoD仍未完成，M5继续。

### 19.13 客户端附着执行期原截止与计时器排空（2026-09-30）

**实现**：b4febc5/ADR-057，#153完成。每个已登记内部attach默认固定deadline worker，先登记后运行；原successReceivedAt、同源clock、min(hint,15000)不续期。禁用Create返回后接管timer，Create/Change/运行期clock/DisposeAsync的同步前缀及原任务均由该worker在父锁外跟踪。回调仅合并通知，早回调按原remaining重排，1ms只限通知粒度，连续立即唤醒超过32次失败。Control-only/纯预算查询不建timer；交付后不再采样attach clock。

停止立即通知timer收尾并独立发起已知连接/reader/取消，不等挂起I/O。成功先等原deadline worker、原DisposeAsync及caller registration，再同gate检查父撤销>caller取消>deadline失败>普通stop、创建本地reader、原预算末检/重入复查后交付。原初始化失败仍保留原错误；原timer清理多错保完整树，固定来源诊断不Flatten。join完成不表示资源物理释放成功，不承诺硬中断不合作provider或I/O。

**验证**：新增48展开例。首轮212=205PASS/7FAIL证实已关闭连接的reader提前构造抢走真实停止原因；调整顺序而非放宽断言后223定向通过。旧57生命周期测试启用手动timer但不关闭生产功能。受闸Task验证真实attach→deadline→原Dispose→provider Fire尾部await链；Guard先确定原Task获胜，不把保护Timeout当业务错误。Fire尾部不是暂停生产OnTimer本体，caller实际在途排空没有新增同等级证明；不外推全部clock锁外或真实TLS握手。

首轮变异第5项因fake内部下限断言而Guard，无效、停止并恢复；正文直接检查due后新序列完成。最终目录`outputs/m5-client-deadline/mutation-20260930-164647-29de2bd5/`：8轮运行期红例1/1/7/7/1/1/4/3，无编译/框架/Guard充数；恢复原bytes并重新构建223绿。主线程独立解析10份变异TRX。源码SHA：deadline `9032e6aacfe5ace4800b5e13ddc3290225dce07faddd83226b37769a0138077a`；lifetime `782caec65023953167dfa7a26636a724dfafe72a67eb56c035d3730b191bdef3`。

**Last code commit：b4febc5（5files/+2018/-26）。Working tree at validation：该提交源码/测试，最终验证后未改，之后仅文档记账。** 主线程逐条解析`outputs/m5-client-deadline/final-{Debug,Release}_*.trx`十二份：每配置**2899 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport1942/Acceptance393）；solution与IsolatedAcceptance双配置四build零警告错误，恢复源码SHA一致。原真实TLS用例随全量执行，未改旧断言；无M4/网络/防护/App改动，无GUI/新包/Python打包重跑。

**下一片**：#155可清零VideoHelloWire完整单数组wire、SensitiveFrameWriter在原Write/Flush退出后全域清零，独立向量/原数组引用/多错保真测试。此片未开始编码；之后仍须高层AttachVideoAsync真实第二TLS actual pin/proof、严格ACK SessionId、Control唯一reader/远端关闭主动感知、采集显示及十分钟DoD，M5未完成。

### 19.14 敏感hello单数组wire与原I/O后清零（2026-09-30）

**实现**：00ecff3/ADR-058，#155完成。新增VideoHelloWire直接UTF8写212字节完整wire（大端208前缀+六字段JSON），nonce/proof借用不改、不产生秘密Base64 string；writer接管非null原数组后所有入口失败也finally全域清零，精确长度与上限校验通过才一次原Write/Flush。同token、无新timer/CTS、无副本/关闭流/重试，原ValueTask消费一次，不取消代理提前清零；原Task多错保持有序嵌套/重复实例，单错原样throw。旧VideoHelloFrame/FrameWriter/parser/public表面不改。

**测试/审查**：新增64展开（wire17/writer47），原parser110不动，最终定向174绿；独立Python向量重新生成并核对两条完整wire。原Write Memory底层数组恒等、offset0/count全部；同步前缀及挂起Write/Flush取消后仍等原I/O，退出全域零；多错树、source同步/挂起故障/取消单次消费均覆盖。source故障缺口按审查补12例，无生产修改。无秘密string/额外副本分配仅源码审查确认，不能把行为测试扩展为TLS内部内存擦除证明。

变异证明改进：观察器改正向发现真实registered await Task再Assert.Same原Task，防WaitAsync代理让旧观察器只Guard；非法长度测试预放IO，漏检直接返回成功导致Assert.NotNull失败而非卡闸Guard。有效8轮由`outputs/m5-sensitive-wire/mutation-20260930-serial-02/`轮1–7与`...serial-03/`轮8组成，红例2/2/2/2/3/2/2/2，17个合同红例；前7未在最后仅长度测试放闸版本下重跑，不冒称同目录完整8轮。serial01轮5/serial02轮8的Guard不计kill，原证据保留。最终原bytes恢复并重建174绿；主线程独立解析TRX并核对SHA。wire `51939de83332e057c6e21a58f91ff96be5271d852eaa54a09cc8f62d160dd726`，writer `4639f38b8c259b5de15113ee416be0032beee000d6b0d87cd92047144a3b44c6`，writer测试 `ce210cce95f7f3daf168db1339d3ec7021c53f3e2adea4493d65ab03957bd6b9`。

**Last code commit：00ecff3（5files/+1265）。Working tree at validation：该提交源码/测试，最终验证后未改，之后仅文档记账。** 十二份`outputs/m5-sensitive-wire/final-{Debug,Release}_*.trx`逐条均Passed：每配置**2963 PASS/0失败/0跳过**（196/299/3/66/2006/393）；solution/IsolatedAcceptance双配置四build零警告错误。无M4重跑、改网、防护修改、App改动、GUI或新包。

**下一片（只读方案，尚未实现）**：新session partial先internal真实接线，不先改public：Control冻结Identity构建ConnectionTarget，生产TlsClientConnector和actual PresentedCertSha256，封闭CreateVideoAttachProof；hello原数组所有权安全移交，token-only FrameReader严格只消费一个ACK并比对父SessionId，后续帧不预读。失败必须持有自己成功登记的child再StopAndJoin，重复/重入输家不能读全局child去stop别人，原worker内不得自join。实际客户端connector本身不做同子网授权，原Host同子网→准入→TLS门禁不绕过；loopback策略仅测试，不冒称生产允许127。session未保存原TransportTimeouts，默认值须显式定案。随后public封闭读帧外壳、Control唯一reader/远端主动关闭、采集显示/十分钟DoD仍待完成。

### 19.15 客户端真实第二TLS与严格ACK（2026-09-30）

**实现**：8ae502b/ADR-059，#157完成。新增internal AttachVideoCoreAsync默认生产TlsClientConnector；Control冻结身份重建独立地址/pin目标，默认TCP3秒/TLS5秒叠加success原预算。校验第二连接DeviceId/地址/端口/expected pin及PinsMatch，用实际PresentedCertSha256计算proof；真实客户端与Host原同子网/准入/registry/router联测，不绕生产门禁。instance工厂仅internal可信接缝，须返回新独占第二连接。

敏感hello原数组移交后等原Write/Flush退出，helper自有nonce/proof清零。ACK token-only，uint域先验1..4096，严格parser再匹配SessionId，错会话固定video-attach-ack-session-mismatch；payload成功取得后的各路径finally清零。精确只消费一帧，不drain/探EOF/预读，第二ACK留给视频reader以video-magic拒绝。资格/登记/捕获本child在同gate，输家不能stop赢家；失败在原attach退出后外层join本child，普通视频失败不关闭Control，成功后不追加终检。稳定诊断与primary双向引用包含归并，仅合并顶层，不Flatten或修改原树重复叶子。

**测试与审查**：新增89展开（组件70/真实TLS19），独立HMAC、随机nonce、actual换证拒绝、粘连首帧/分片ACK/错会话/第二ACK、偏差身份、原wire/ACK数组零、原I/O及慢join等待、重复和clock重入、异常树均覆盖。首次solution Debug成功但2个nullable警告，补target非null后零警告；首次宽筛137=136绿/1红，原因是Task.WhenAll排序假设，修为逐项比较原Task实际树的引用/顺序，不放宽生产合同。独立审查补地址非同引用断言，最终新89定向绿。

**变异**：`outputs/m5-client-attach/mutation-20260930-serial-01/`八轮红例2/6/8/4/1/1/1/2，共25个目标运行期断言；分别ACK会话匹配/清零/外层join/反向包含/原connect Task/DeviceId/地址副本/敏感write等待。主线程逐条解析10TRX，无Guard/编译失败充数；第8轮仅删除等待，未跑WaitAsync候选。7373字节源码与备份精确恢复，SHA256 c0b8d17d6205ff314bd361f6a835c67b1243b1cc0d222d57352d9dff16a8f8bd；385保护文件、34恢复产物核验一致，恢复重建89绿。

**Last code commit：8ae502b（5files/+2320）。Working tree at validation：该提交源码/测试，最终验证后未改，之后仅文档记账。** 主线程逐条解析十二份`outputs/m5-client-attach/final-{Debug,Release}_*.trx`：每配置**3052 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport2095/Acceptance393）；solution与IsolatedAcceptance双配置四次完整Rebuild零警告错误。源码/测试哈希与恢复版本一致；ADR在验证期间新增属已知文档变更。未改产品App/权威规格/网络/防护，无GUI/新包/M4专项或Python打包重跑。

**边界与下一片**：既有83字节proof transcript含nonce副本未清零，但不含sessionToken/proof；不外推TLS/Stream/HMAC内部副本擦除。expected/presented正常相同，替换入参可能等价，不能捏造该变异必红；FrameReader更底层已丢失的多fault不可恢复。本片internal接线不等于public视频或产品看屏完成。

继续先做internal显式Control断连monitor，默认Control-only/旧internal入口不增加读。拟由成功登记child的入口在同gate登记唯一父拥有worker，重复/重入输家不启动它；至多一次1字节None-token原读，EOF/故障/额外数据撤销并独立请求Control close/child stop，原worker不自join，父join覆盖原读与关闭。保留owner纯逻辑撤销，原读同步前缀/AsTask/多fault可追溯；原始诊断与将来DisposeAsync报告分开，按本地停止与原Task已完成的观察顺序分类，不声称能证明关闭竞态的物理原因。不开新心跳/消息协议，不探测无EOF/RST网络黑洞。后续public封闭外壳固定使用monitor路径，再采集/JPEG/显示与十分钟DoD。此下一片为经源码核对的实施方案，尚未编码（后续实现见§19.16）。

### 19.16 显式Control断连监控（2026-09-30验证，10-01记账）

**实现**：f3dbccc/ADR-060，7files/+2309/-7。internal显式monitor入口成功登记child后同gate登记父拥有Worker；旧入口/Control-only不增加读。至多一次1字节None-token原读，EOF/故障/额外数据同gate撤销，额外数据固定control-unexpected-data；锁外独立请求Control close与child stop，不自join。父共享WhenAll完整等Control close/child join/monitor，含ReadAsync同步前缀、AsTask及原Task。纯逻辑owner撤销和公共同步Dispose原合同不改。

**诊断/边界**：原Task多fault完整树保留；本地停止标志仅反映原Task发布/完成的观察次序，不证明close物理因果，不按IO/ODE类型过滤。SkippedLocalStop未发读、标志true。不新增心跳/消息协议/黑洞检测。已覆盖reader成功后提交前的撤销与owner恰一次释放，既有已交付帧不被回收；最窄Committed已设置但原worker/外层Task尚未完成窗口尚未确定性固定，不声称已覆盖。真实TLS事后await只证明最终退出，完整join强证据来自组件闸门/await链。

**验证**：新增65展开（57组件/8真实TLS），与原attach回归合计154定向绿。首轮75中3红来自SkippedLocalStop期望、原Task完成观察代理持gate竞态、SslStream私有字段错误假设；按合同修正并实测.NET10.0.12 NestedState枚举。review build曾有xUnit2012，等价改Assert.Contains后最终无警告，旧失败日志不覆盖。两次独立只读审查，补父完整异常树/慢close不阻塞child stop/attach与frame提交竞态；只贯通原rent/frameRead实例接缝，不替换认证或monitor。

**变异**：六单点6/6有效，目标红例1/2/2/2/3/5共15；第4另有附着前ODE不计目标，不称3/3，无Guard/编译失败充数。首runner误判xUnit FAIL通知后恢复再续跑；三个独立证据目录保留，未重复已有效单点。主线程核验13TRX、3源码原bytes、320保护SHA、34恢复产物和29自有Job排空记录；基线与6恢复均154绿。无全机进程扫描结果，不绕过被阻拦的API。

**Last code commit：f3dbccc。Working tree at validation：该提交完整源码/测试，最终验证后未变。** 本轮主线程执行串行solution/IsolatedAcceptance双配置四次完整Rebuild并读取日志，均0警告/0错误；逐条解析12份final TRX：Debug/Release各**3117 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport2160/Acceptance393）。303源码/测试/工程/脚本SHA前后一致。原证据`outputs/m5-control-monitor`，无M4或Python打包重跑、无新包/GUI、无产品App/权威规格/网络/防护修改。

**下一片**：封闭public AttachVideoAsync/AuthenticatedVideoSession/原Task ReadFrameAsync及父子IAsyncDisposable。固定monitor生产路径；外壳提交前预构造、提交后无撤销/取消终检；重复输家不清理赢家。异步释放等原join（父另等已接受的public attach投影），原始异常完整树严格报告，不按本地close时序或IO/ODE类型抹错；这意味着主动关闭也可能报告异常。public签名/报告层与确定性测试尚未实现，先完成本片，不把规划写成代码。之后采集/JPEG/显示/有界队列与十分钟DoD；M5仍未完成。（后续落地见§19.17。）

### 19.17 封闭public视频接口与完整异步释放（2026-10-01）

**实现**：511ca6d/ADR-061，10files/+2228/-5。父AttachVideoAsync固定真实第二TLS/严格ACK/Control monitor，child预建sealed AuthenticatedVideoSession；无公开流/连接/秘密/工厂。ReadFrameAsync原Task透传token，帧交付后归caller。资格/重复同步拒绝不清理赢家，成功后投影不追加终检。父缓存DisposeAsync完整等raw join+已接受投影，子仅自身raw join；稳定完整原错统一Aggregate报告，重复调用共享Task/异常，主动close的IO/ODE/OCE不隐藏。

**审查修复**：自定义scheduler的QueueTask可抛错或重入，旧StartNew后发布投影会漏追踪；改未启动空Task屏障→真实await投影→同gate发布→Start，调度失败由原faulted屏障保留，投影停止并完整join自己的attempt，无自join。测试补投影受闸时子释放先结束、有效caller取消、sync/throw/reentrant调度、主断言不被cleanup覆盖及20秒总清理预算。首build7个xUnit2014/2个CS4014，保持同步异常合同修正测试写法。两轮实施审查无剩余阻断。

**验证**：新增50展开（42组件/8真实TLS），独立HMAC/真实生产TLS路径、原await链、帧owner、取消、异常树、父子作用域均覆盖。八单点有效目标1/2/2/3/3/3/3/3共20红，跨`outputs/m5-public-video/mutation-20261001-043937-00a86d4f`的1/3–8及`mutation-02-assert-null-20261001-050104-b231e5dc`的2；首轮组件Guard及两个非显式ODE不计，旧证据不覆盖，不重跑7有效。第2最小补显式成功Assert.Null（Guard在外）后目标2红，恢复50绿；主线程逐条解析14TRX并重算两生产/最终测试SHA。311保护文件与4恢复产物核验是变异范围，不套用为最终Rebuild产物字节不变。

首次final Debug总3166PASS/1FAIL（Transport2209/1）来自遗漏旧AttachBudgetTests public方法白名单，与新合同冲突；仅增加AttachVideoAsync/DisposeAsync预期，其他预算/秘密/构造封闭断言保留。该轮在Debug后停止，未称Release已跑。独立final-02重跑四完整Rebuild及双配置全量：**Debug/Release各3167 PASS/0失败/0跳过**（Core196/Protocol299/Integration3/Security66/Transport2210/Acceptance393）。主线程逐条核验12TRX及日志，四build零警告错误，307源码/测试/工程/脚本SHA前后及核验时一致；Last code commit **511ca6d**，Working tree at validation为该提交源码/测试，之后仅文档记账。

**边界与下一步**：投影受闸是core已完成/public未完成，不声称固定底层Committed后worker未完最窄窗口；TLS事后await只证明最终退出，强join来自组件。Capture与Sessions仍空，App未接Transport，Host router/frame source仍internal，产品尚不能看屏。下一片实现Core既有接口的GDI主屏raw+WPF JPEG编码（net10.0-windows，不加WinSDK版本TFM），合成像素/离屏GDI先验证；主屏5FPS/50%/quality60由上层串行有界调度。再补Host显式public安全装配（不暴露秘密/流、不加friend绕层）、raw/encoded两级容量1–2队列、WPF显示与十分钟DoD。无M4重跑、真实桌面采集、GUI、新包或现网/防护/App修改。

### 19.18 主屏 GDI 采集与 JPEG 编码（2026-10-01）

**实现**：99dfd76/ADR-062，14files/+3096/-12。Capture既有接口落地：GdiScreenCaptureBackend只读主屏物理模式、设备本地坐标、top-down BGRA alpha255，清理native成功且显示身份/模式复查后交付；WpfJpegFrameEncoder最近邻缩放/BGR24/WPF JPEG、实际payloadLength、帧ID不回绕、输入raw借用。PooledByteOwner精确长度/清零整个租用数组。公开operation单飞、不排队，直接await原worker，取消不提前归还原操作使用的资源；两个internal只读worker观察非产品替换入口。Capture只加UseWPF不加版本化WinSDK；测试项目入solution（CLI将旧Acceptance配置块等价重排）。

**发现与修复**：初145测144PASS/1FAIL，真实WPF Save吞MemoryStream.Write超限NotSupportedException并正常返回(length0)。新增JpegOutputStream锁存底层错误，Save后原错优先终检，吞错后正长度片段同样拒绝；Save自身与底层独立错误保留原树。追加12例共157（GDI92/JPEG57/owner8）。无扩展错误合同的GDI API不猜LastError；清理顺序/失败安全兜底与原错保留均覆盖。独立复审无剩余阻断。

**变异实证**：`outputs/m5-capture-jpeg/mutation-20261001-065758-565d8928`中3–8有效24断言红；1/2仅8个Guard，不计。测试helper改从公开operation的任意awaiter Task正向登记后再Assert.Same原worker，不能将就绪条件绑定正确目标。独立`mutation-20261001-073113-a68737fa`仅复验1/2，实际身份断言5+3红，恢复157绿；八项跨版本联合有效32目标红（5/3/3/4/1/9/6/1），不称同批八轮。主线程逐条核验10份变异TRX的32断言与8Guard分类；14保护文件每轮恢复，旧181证据不改。两helper最后4处xUnit2031等价改Assert.Single谓词重载，未重跑六有效变异。

**Last code commit：99dfd76。Working tree at validation：该提交源码/测试，最终验证后未改。** `outputs/m5-capture-jpeg/final-20261001-074525-215929`串行solution/IsolatedAcceptance双配置四次完整Rebuild均0警告/0错误；两配置全量各**3324 PASS/0失败/0跳过**（Core196/Security66/Protocol299/Integration3/Transport2210/Acceptance393/Capture157）。主线程独立解析14份实际TRX逐结果/counters与四日志，并核验319源码/工程/测试/脚本SHA前后及当前一致；非3167+157推算。六命令退出0，原runner和直接子命令均退出；没有全机进程扫描或Job级排空结论。

**边界与下一片**：合成真实JPEG/离屏GDI通过，不是实时桌面或混合DPI验收；32MiB仅payload上限非总内存；不外推十分钟稳定。Sessions仍空，下一片实现独立会话两级容量1–2 raw/encoded DropOldest生产管线，初始5FPS/50%/quality60，不让网络阻塞采集；Stop完整join原Capture/Encode和清理，确认为内部队列Stop的ODE仅在干净全收尾后转EOF。再Host显式public装配（需会话stop/join，不仅read委托，不能加friend绕层）、WPF显示与十分钟DoD。M5仍未完成，不重跑M4/不改现网或防护/不绕过桌面权限/不出新包；App未变。

### 19.19 Sessions 双级有界帧管线与完整停止（2026-10-01）

**实现**：50d54af/ADR-063，7 files/+3659（solution、FramePipeline/Sessions工程、3测试文件及测试工程）。生产`FramePipeline`复用Core队列，raw/encoded各容量1或2、默认2、DropOldest；固定5FPS/0.5缩放/quality60、Auto=false。每轮按实际采集启动时刻计算200ms剩余预算，超时不补帧、不追赶；慢编码/消费不制造无界积压。capture/encoder为专用借用，须持有至Completion完成，管线不释放它们；这不是拥有型factory已完成。

Start仅一次，CAS决定Start/Stop赢家；构造不采集。Start发布屏障覆盖真实循环引用，创建循环后部分失败仍收尾。internal Start发布观察接缝只向friend `LanRemote.Sessions.Tests`开放，不作为产品跨层入口。原Capture/Encode的ValueTask只AsTask一次并直接await，含同步前缀/异步尾部；raw活至原Encode退出，迟到产物先归本地ownership再停检/回收。交付帧归caller，不作后置取消检查；owner清空后只尝试一次Dispose，drop/清理错误不吞。

StopAsync/DisposeAsync/Completion共享完整join：两个原循环及raw.Stop、encoded.Stop、Cancel三个独立清理任务全退出后才释放CTS；慢disposer/callback不串行阻止其他清理。保留原多fault全部根/嵌套对象，不Flatten；仅非Faulted操作的本管线已请求token取消可作正常取消，cleanup OCE与Faulted任务内OCE仍报错。EOF两阶段：队列读取可取消且单消费者；内部队列停止后先释放读槽，再不可取消地直接await Completion，干净才null，否则同一稳定Aggregate错误。Completion不join read，避免互等；不合作原操作不承诺停止时限。

**初轮证据与修复**：`outputs/m5-frame-pipeline/initial-{build,test}.log`及`initial-Debug.trx`保留：82例80绿/2红，capture/encode异步尾部两例均`Assert.Same Expected: null`。fixture的Tail.Entered可早于Invoke写回Worker；修为先观察InvocationExited发布，再断言原await身份，不是生产取消/join缺陷。追加Start/生命周期回归后`fixed-Debug.trx`为104绿（Flow36/Lifecycle60/Start8）。initial-build的xUnit2031等价改Assert.Single谓词重载；fixed-build另有CS4014，显式`_ = AssertLoopFailure(...)`消警告，断言仍同步执行；两份旧warning日志不改成零。

**八单点变异**：`outputs/m5-frame-pipeline/20261001-093617-singlepoint8/report.json`，主线程独立解析19份实际TRX并对照失败消息；8/8有效，20目标断言红，0Guard/其他错误/编译失败。各轮恢复源码后Rebuild及同目标全绿；Debug基线104绿，最终Sessions双配置各104绿。

| 单点 | 实际目标红 | 实际失败证据（不外推） |
|---|---:|---|
| 1 删除Start发布等待 | 1 | Assert.Same(StartPublished, 实际await) |
| 2 EOF改可取消代理 | 2 | Assert.Same(Completion, 实际await) |
| 3 Capture改取消代理 | 2 | 首轮原Capture await身份；未进入后续取消时序 |
| 4 Encode改取消代理 | 2 | 首轮原Encode await身份；未进入后续取消时序 |
| 5 Flatten原fault树 | 8 | 根数1→2、3→5；未运行到后续顺序/实例断言（另8对照绿） |
| 6 采集后固定等200ms | 2 | 工作75/199ms应余125/1ms；0ms对照绿 |
| 7 删除取时后stop检查 | 1 | Capture调用预期1实际2，迟到调用已放行，非超时 |
| 8 join漏encode循环 | 2 | 探针明确Assert.False报CoordinateStopAsync提前退出；不是Guard或join注册数断言 |

**最终全量与独立核验**：`outputs/m5-frame-pipeline/final-20261001-094907-708217/summary.json`及原日志/TRX；solution与IsolatedAcceptance双配置四次完整Rebuild均0警告/0错误，六命令rc=0且日志SHA核对一致。主线程逐条解析16TRX/counters，Debug/Release各**3428 PASS/0失败/0跳过**：Core196、Security66、Protocol299、Integration3、Transport2210、Acceptance393、Capture157、Sessions104（36/60/8），不是基线加增量推算。

**Last code commit：50d54af。Working tree at validation：该提交源码/测试，最终验证后未改。** final冻结328项（src/tests/tools/scripts除bin/obj/.git及指定根文件，含HANDOFF），前后/各命令边界SHA一致；本轮编辑前重新核对当前328文件全部匹配。变异55项保护包含旧证据等，是不同scope，不能混称328项变异保护；本轮文档记账后HANDOFF变化属明确例外。final全机进程枚举被安全策略拦截，只确认runner与6个直接子命令已退出，不证明间接子进程/全机无残留，不沿用变异轮的空进程快照结论。

**边界与下一步（尚未实施）**：尚未App接线、实屏或十分钟DoD，M5仍未完成。先做Core生产factory/producer安全contract + Sessions拥有型factory，再做Transport每已认证会话生命周期public装配：ACK flush成功后才创建producer，停止必须join，禁止迟到read触发初始化；不是只塞read委托，也不加friend绕层。Host计划尚未落地，随后才App/WPF显示与十分钟DoD。本轮仅读证据和更新四份记账文档；未运行dotnet、未改代码测试、未提交、未建任务。不重跑M4、不改现网/防护、不绕过桌面权限、不出新包。

### 19.20 会话拥有型视频生产工厂与封闭公共契约（2026-10-01）

**实现**：`9cede86`/ADR-064，6 files/+2583（Core两个public接口、Sessions工厂及适配器、两份Sessions测试）。`IVideoFrameProducerFactory.CreateAsync(Guid sessionId, CancellationToken)`只接受非空关联ID，返回未启动、单会话专用的`IVideoFrameProducer`；后者仅公开`Completion`、单次`Start`、`ReadNextAsync`、`StopAsync`及`DisposeAsync`，输出是`EncodedFrame`/EOF，不暴露Stream、TLS连接、session token或认证状态。ID不是凭证，工厂不检查认证；不得把“已有factory”误记成Host已安全装配。

`FramePipelineVideoProducerFactory`构造仅验证并保存两个依赖创建委托、容量1或2及可选时钟，不触发采集；每次`CreateAsync`以不携带取消参数的原`Task.Run`承接委托同步前缀，直接等原创建操作，取消不借`WaitAsync`代理提前返回。顺序取得专用capture→显示列表唯一`IsPrimary`（不假定枚举首项）→专用encoder→未启动管线；各可到达失败/取消点均完整回滚已取得资源，原操作返回后才检查取消，交付后创建token不再控制生产者。工厂、委托目标和时钟借用不释放；专用依赖不得跨会话共享/由外部并发使用和释放，若两个接口是同一对象按引用仅清理一次。

适配器在构造时即观察管线`Completion`，生产自行失败也会在管线退出后清理专用依赖；`StopAsync`/`DisposeAsync`共享稳定完成任务，先等管线原双循环/清理完成，再独立调度两路依赖释放，异步释放优先于同步释放，不因一路同步前缀阻塞另一方。`ReadNextAsync`成功帧交给caller负责释放，不追加取消终检；caller取消仅影响队列读取，不停产；EOF或管线故障都等待清理完毕，干净返回null，错误报告含管线及依赖全部原fault根/嵌套树，不Flatten、不按OCE/IO/ODE吞错。清理不join read以免读EOF反等形成环；依赖回调自身不得等待此producer的Completion/Stop/Dispose。无法强行中断不合作的原操作。

**首次结果与等价修复**：`outputs/m5-frame-pipeline/factory-initial-{build,test}.log`记录工厂首轮200绿但CS4014/xUnit2031两警告，等价修正测试写法、不改合同。`producer-initial-build.log`四个CS0246出现在边写测试边编译的未完夹具，不冒充正式通过；完整夹具后`producer-fixed-Debug.trx`为217/218，唯一tail故障注入过早触发`Assert.False`，修复夹具释放顺序后`producer-repaired-Debug.trx`为218/218，`producer-repaired-build.log`零警告零错误。旧红/警告原始日志保留，不覆写为绿。Sessions最终218=旧FramePipeline104+本片114（Factory96/Producer18）。

**七轮单点变异**：`outputs/m5-frame-pipeline/factory-mutation-20261001-104604/report.json`及14份原始mutant/restore TRX；按创建取消代理、漏采集后取消、错误地取首显示、停止只等管线、清理WhenAny、以值相等去重、清理仅取await首错，目标Assert红依次3/1/2/2/12/6/3，共29，0Guard/其他错误；逐轮恢复原目标全绿，最终分类均`killed_by_assertion`。旧分类器把第1轮误记`not_killed`，审计与本轮直接解析原TRX再证实3条真实合同断言红；不能沿用旧标签。变异冻结21文件，绝非final334文件的同一保护scope。

**最终全量/记账边界**：`outputs/m5-video-producer/final-20261001-105537-153331700/summary.json`、原日志及16份TRX；Debug/Release各**3542 PASS/0失败/0跳过**（Core196、Security66、Protocol299、Integration3、Transport2210、Acceptance393、Capture157、Sessions218），逐份实际UnitTestResult与计数核对，不以旧数相加代替实测。solution及IsolatedAcceptance在两配置四次完整Rebuild均零警告/错误；六命令rc0及各自日志SHA已核对。冻结scope334项逐文件SHA在六阶段前后及本轮编辑前当前一致；含HANDOFF，文档记账后该项变化属明确例外。final runner退出0且六直接dotnet子进程由runner等待；`system_process_enumeration_verified=false`，不能声称间接进程或全机排空。

**下一片（尚未实施）**：Transport在每个已认证的Host会话ACK flush成功之后创建生产者，并与该会话父子生命周期绑定，停止完整join且不让迟到read初始化；认证门禁必须在Transport而非仅凭Guid或工厂，不能只传read委托/借friend绕层。其后才将帧接App/WPF解码显示并做实屏与十分钟DoD。Host/App未落地，M5未完成；**此处“本轮仅修改四份文档”只描述§19.20上一片记账轮，不描述随后§19.21的代码片**。上一片没有重新运行dotnet、修改代码/测试、提交、建任务、重跑M4、改现网/防护、绕过桌面权限或出新包。

### 19.21 认证Host视频工厂路由与原任务诊断（2026-10-01）

**实现范围**：`2e48cb4`/ADR-065，9 files/+1415/−23。Transport公开`TransportHost.CreateWithVideo(...)`，把借用的`IVideoFrameProducerFactory`装入认证Host的`FirstFrameRouter`；旧shared `IVideoFrameSource`/internal路由及Control-only入口保持兼容，不以裸Guid/工厂代替注册表VideoAttach的同会话地址、证书、nonce/proof资格。完成第二TLS首帧与Attach后，ACK成功写入且flush、阶段/信封检查通过才调用工厂；工厂原同步前缀/异步创建始终在被路由持有并join的transfer内，迟到创建结果接管后即停止/释放，不由迟到read重新初始化。取得producer后先注册到停机可触达状态，再于锁外调用`Start`（避免可注入同步前缀占锁阻塞Stop）；停止抢先Start时，只有生产者明确证明**这一原`InvalidOperationException`实例**为stop-before-start拒绝才可视作正常，不按异常类型/消息泛吞。证明探针本身若抛IO/ODE/OCE或嵌套Aggregate，必须保留原Start错及独立探针错，不覆盖主错。

**生命周期与错误边界**：单个已接管producer供固定来源读取，不对每帧重调用工厂；Control关闭/Host停止/peer关闭及video lease撤销均请求关闭、取消、Stop，等待transfer、peer、关闭与取消、Stop及Dispose的原操作退出，已交付帧归消费方；不合作原操作不能强制限定完成时长。保持每个原Task的所有fault根与嵌套，不Flatten、不因IO/ODE/OCE类型静默抹去来源或清理错误；Stop/Dispose若返回同一原Task，多次观测其错误树只能按**原Task身份**去重一次，不能以Exception引用跨不同原Task去重，故不同原Task复用同一异常实例仍保留两个故障位置。此处只是本Video路由的局部join，**尚非Control→Video注销后全局父子join或Host完整Dispose承诺**。

**发现、修复与测试**：初轮Host边界57/57绿；回归发现可注入`Start`占锁阻止并行`Stop`、以及Stop先CAS抢占后的正常拒绝分类过宽，改锁外Start加原实例证明后80条边界绿。随后反复观察同一Stop/Dispose原Task导致错误树重复，曾有4红；按原Task身份去重后83条边界绿，Transport全量当时2252绿。再补不同原Task携同一异常实例仍保留双根3例，最终Transport2255绿；证明探针自身IO/ODE/OCE/聚合抛错四展开覆盖。旧失败不改写为绿；边界数量与最终完整TRX各自独立，不推算全量。

**终验与可复核失败证据**：首轮`outputs/m5-host-video/final-20261001-120711-095134700/summary.json`状态failed/exit1，首Debug solution Rebuild rc0且0警告/错误，但`tests/LanRemote.Transport.Tests/FirstFrameRouterVideoFactoryTests.cs`在并行编辑时SHA从`cdb374da…`变为`de15dbc…`，冻结发现变化自动停止；`actual_tests=0`，不可翻绿或说它验证了最终代码。随后独立`outputs/m5-host-video/final-20261001-122116-867239300-retry/summary.json`状态passed/exit0；主线程重新解析16份原始TRX各UnitTestResult与Counters，Debug/Release各**3589 PASS/0失败/0跳过**：Core196、Security66、Protocol299、Integration3、Transport2255、Acceptance393、Capture157、Sessions220。四份Debug/Release solution及IsolatedAcceptance完整Rebuild原日志均0警告/0错误，六命令rc0、六日志SHA一致；337项实际源码/测试/工具/脚本及指定根文件冻结前后逐文件SHA一致，记账前再核对当前337/337一致（含HANDOFF；本节更新后该文件成为明示文档例外）。runner退出0且六个直接命令均等待退出；安全策略拦全机枚举，不承诺间接子进程/全机排空。本次文档整理**未**另跑dotnet；这些数值来自此前终验，不将§19.20的“仅文档轮”套到本片源码/测试变更。

**边界与下一关（M5未完成；仅§19.21当时）**：当前Registry在proof成功时直接消费一次性资格，路由后置截止异常尚缺明确释放预约合同；先在Registry同一锁内预约proof资格，再使`AttachAsync`成功后检查异常可释放预约且**不得恢复**已经消费的一次性资格。随后让Control注销/会话token零化后可join已预约Video的**真实结束**，再让Host预算`StopAsync`如实报告超时、`DisposeAsync`完整无界排空已登记原操作；不以现有局部路由join冒称Host产品完全闭环。这些Transport生命周期缺口已由后续`b3e1273`/§19.22处理，**此处历史缺口不是当前停点**。产品App仍未接线、未实屏/十分钟验证，**M5未完成**。M4不重跑，现网/防护不改，不绕桌面权限、不加包或出新包。

### 19.22 认证Video父子预约、Host预算Stop与完整Dispose（2026-10-01）

**实现**：`b3e1273`/ADR-066承接`45dd8db`/§19.21/ADR-065，16 files/+2225/−423（仅Transport产品代码与Transport.Tests；不是本次文档轮的四文件统计）。`SessionRegistry.TryAttachVideo`在最终proof、同源IPv4、证书、取消与期限检查后，于同一把锁内消费一次性资格并预约唯一`VideoAttachLease`；可重入取时后重查登记表身份与状态。`FirstFrameRouter.AttachAsync`成功后即将lease交父持有，三项后检（阶段、信封、登记等待预算）任一失败仍走关闭/收尾，**不返还已消费资格**。lease的`Revoked`在Control注销、登记表持有token清零时即发，且不在锁内执行任意外部回调；父在锁外等待同一lease的`Completed`。子只有等`RunAsync`主体及Host原关闭任务、原Control owned操作退出，才发`Completed`；撤销通知/请求关闭不等于子真正结束。此次只是登记表自有token零化，不保证DTO字符串、TLS或WPF等内部副本擦除。

**停机与异常**：Host `StopAsync`继续是有预算报告：accept循环/真实连接未完成如实记录；根取消回调若单独未退出，可单独抛`TimeoutException`，**不伪造连接数**。`DisposeAsync`并发调用共用稳定任务，启动自己的停机并等当时在途Stop、accept原任务、根取消、Registry的原连接/force/linked CTS全部完成，随后按顺序释放Registry与Host两根取消源；不合作原回调不作硬中断或有限完成承诺。保留全部在途故障原树而非只收await首错。**历史已完成Stop的fault仍在各自返回的原Task上供调用方await**，Host内部`HostLifecycleErrors`只为每类保留首错（至多七类），`Dispose`不重复报告历史已完成Stop；对其开始时仍在途的Stop故障才完整汇总。不能把内部有界诊断当作所有Stop错误的完整总账。

**六轮单点变异**：先验基线Debug Rebuild 0警告/错误、六目标展开10/10绿。六次分别剔除父lease、token零化/即时撤销、关闭join、Control等子退出、Host/Registry完整排空、linked CTS释放/双故障报告；每轮至少一个目标TRX明确Assert失败，依次**1/1/4/1/1/1，共9条Assert红、0条Guard/清理超时红**，均恢复原bytes/SHA并在同一目标重跑为绿；变异及恢复构建均rc0/0警告/0错误。第5轮`stopFirst=False`红而`stopFirst=True`仍绿，故只说**6/6单点被目标Assert检出**，不可称“六轮所有理论参数行都红”（实际5/6轮全部红）。`outputs/m5-host-video/parent-mutation-20261001-134448-012529700/summary.json`与`parent-mutation-final-20261001-134942-043130500/mutation-evaluation.json`保留原TRX及归类，保护清单35文件，不等于final337项。

**先期失败证据不覆盖**：首变异尝试`parent-mutation-20261001-133418-836919600`基线build虽rc0却有一条xUnit1031警告，停在变异前；第二尝试`parent-mutation-20261001-133849-902594700`冻结脚本误递归`outputs`造成路径超长，build及变异均未运行。修复测试警告与冻结范围后的第三轮六变异真实运行，但旧runner硬编码Transport预计2278，实际新增并发Completion测试后2279：Debug最终原TRX **2279/2279绿**仍触发严格旧计数而停，Release在`parent-mutation-final-20261001-134942-043130500`补充目录 **2279/2279绿**；不改旧证据状态，也不为迎合2278删测试。上述是Transport定向/阶段证据，非下段全量的替代。

**最终完整终验与工作树**：`outputs/m5-host-video/final-parent-dispose-20261001-135550-001799400/summary.json`及原六日志/16 TRX。逐份实际`UnitTestResult`与Counters核对，Debug/Release各**3613 PASS/0失败/0跳过**：Core196/Security66/Protocol299/Integration3/Transport2279/Acceptance393/Capture157/Sessions220。solution及IsolatedAcceptance双配置四次完整Rebuild 0警告/0错误；六命令rc0、六原日志SHA一致。冻结337项在每段前后逐文件SHA一致，本次文档编辑前当前337/337亦与冻结清单一致；清单含HANDOFF，编辑后HANDOFF是明确文档例外。runner退出且六直接dotnet子进程已等待退出；全机进程枚举受限，不推断间接进程已排空。本次记账未运行dotnet、未修改src/tests、未提交/新建任务；历史失败证据原样保留。

**余项与停点**：Transport父子清理不证明产品App已交付。下一片接App真实Host启停和本地ViewOnly审批、Client高层认证及prompt key，再做WPF有界JPEG解码渲染/完整退出、真实屏幕与十分钟DoD；此前不把M5记完成。M4不重跑，现网IP/DHCP/DNS/路由/热点/网络类别及防火墙不改，不绕桌面权限、不引入新包。此处“本次只改四份文档”**只指§19.22的记账轮**，绝不覆盖此前`b3e1273`代码/测试提交。

### 19.23 产品ViewOnly接线与真实回环终验（2026-10-02，未提交工作树，M5进行中）

**结论先行**：本轮实际修改了产品App、Transport、Discovery、Core及`tests`，并新建了App的Services两个`.cs`及Acceptance七份测试；这些是**未提交的工作树内容，不存在本轮App代码提交号**。`b3e1273`是最近**已提交代码**的Transport历史基线（§19.22，双配置各3613 PASS），其后的文档提交不能充当App代码提交。当前工作树代码已接Host/Client/WPF，仅在本机限定的真实双TLS loopback中显示合成JPEG；不将M5、产品双机GUI实屏或发布包记为完成。本节是既有证据记账，不是编辑文档后重跑验证。

**产品接线与安全边界（ADR-067）**：
1. Host只有在本机显式勾选允许查看并点击“开启仅查看共享”、磁盘配置安全布尔字段/传输端口严格核实、身份/私钥/本机密钥可用、本机审批门开启且找到合格RFC1918网卡时才用`TransportHost.CreateWithVideo`监听；旧配置的查看开关不自动开启Host。停用查看/审批或关窗先同步阻止新监听、撤销待审批，再等待Host完整Stop/Dispose。`WpfLocalApprovalGate`展示已Shown的真实窗口和供人工核对的短码；批准按钮仅在人工勾选ViewOnly后可用，**并不由程序证明操作者确实核对短码**；Control请求直接拒绝；Block先取消授权再等原窗口`Closed`/Dispatcher收尾，不能用决定已提交替代窗口已关闭。
2. Client冻结连接目标，远端key只从本次`PasswordBox`输入或调用方本次临时参数取得（非命令行密钥入口），**不读取本机访问密钥当远端key**；Control高层认证验证实际TLS pin、serverProof及ViewOnly权限，第二Video TLS严格附着和ACK后才用封闭会话读帧。`RemoteVideoPresenter`用容量1拥有型压缩帧队列、JPEG头与像素预算预检、后台解码、单槽WPF Dispatcher呈现；仅交付冻结`BitmapSource`，不发键鼠事件。窗口/主动断开后保留原网络读、解码、cancel、Control/Video释放任务并join，不拿`IsVisible=false`或计数归零冒充资源排空。
3. 首装确实没有config时，仅在身份就绪后**自动公告本机身份**，零view/control能力、零Host监听；它不是自动共享。坏JSON、缺安全字段或旧配置均fail closed不公告；明确`AllowDiscovery=false`不被首装逻辑覆盖。历史`AllowViewing=true`也不授予本轮监听；只有显式开启成功后才更新view公告，Control始终关闭。证据是隔离目录+无socket discovery夹具，非两机发现或联网观察。
4. Transport Control父会话`GetDisposalEvidenceAsync`为不透明只读证明：必须等**同一原Dispose报告任务**（允许原fault保留）及原Control读/子Video任务真正完成，再核实TCP/SSL双根、timer、reader、未交付owner、CTS/registration等独占根正常释放；不因Dispose fault本身就洗绿，也不消耗/改写原IO/OCE异常树。子会话证书仅internal。Presenter/Client分别固定到同Attempt的原清理Task和原worker，保留所有根与嵌套异常而非按主动关闭类型吞掉。App六类原停机Task逐项等待：只有**客户端**原Task fault、其余五项及全部复核成功，且同一客户端原Task对应的完整资源根释放证书和Host排空成立，才可报`DrainedWithDiagnostics`并退出；其余原fault/取消或根未证实均`Unproven`，未完成为`Pending`。退出虽可带诊断，绝不重写为`Clean`。

**执行过的受控证据**：`ProductRealViewOnlyLoopbackTests`用独立远端密钥/证书、真实产品Client与Transport Host、Control+Video两条真实TLS、测试专用Loopback同子网策略和手动审批gate；审批前无View窗口/Producer，按真实RequestId明确批准ViewOnly后，一帧2×2真实JPEG经过网络、读取和WPF解码呈现在离屏`Show()`窗口，`BitmapSource.IsFrozen`、尺寸及像素有断言。第二次原读取仍在途时夹具模拟用户调用真实窗口`Close()`，原OCE或IO网络终止诊断保留，原Cleanup Task与证书同一，帧owner、连接/审批/registry和Producer原任务最终归还；错误窗留可见诊断与按钮供二次关闭。此端到端夹具的审批**不是**产品真实审批按钮；真实`WpfLocalApprovalGate`的Shown/勾选/拒绝/阻断/原`Closed` join另由`ProductHostApprovalRaceTests`验证。其他产品测试守护配置首装、Host启停、Client原清理历史及App退出分类；这些都不是两台物理机的用户桌面截图。

**单点变异与误判保留**：首装不公告变异使3目标断言红/恢复33绿；坏配置错误公告变异使3红/恢复6绿；App旧六Task分类变异使4红/恢复10绿。仅称相应目标运行期断言，未将各次定向重复数相加当全量。较早首轮xUnit/构建警告、真实WPF `Closed`测试误注错、80ms时序假红及运行时TLS取消可能表现OCE或IO的竞速均保留原始记录；最终零警告不追认这些旧轮为通过，不把某个取消异常类型当唯一判据。

**Last code commit：`b3e1273`（已提交Transport历史基线；本节产品App/Transport/Discovery等`src/tests`未提交）。Working tree at validation：`outputs/m5-host-video/final-m5-host-video-20261002-100918-163800800/summary.json`（status=passed，exit=0）对应2026-10-02当时未提交产品工作树。** 主线程核验16份原TRX的每个`UnitTestResult`及Counters：Debug/Release各**3712 PASS / 0 FAIL / 0 SKIP**，分项Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance469/Capture157/Sessions220；Debug/Release solution及IsolatedAcceptance四次完整Rebuild均零警告/错误；六直接命令rc0、六原日志SHA一致。冻结scope为src/tests/tools/scripts实际文件及指定根文件350项，冻结前后/六阶段边界逐SHA一致，合成SHA-256均`ad8462c3b5784cf995485e90fe5b8d96487379da435bdd1703c7c943eb8feeb9`，Git状态原bytes前后同SHA `afb6f939e5eb5efff2a076c4ade01d1ca7bb99460cc9ba745dc454583f7e62f2`；包括未跟踪Services及七份Acceptance测试。**此刻HANDOFF修改属于冻结清单内的文档例外，不能再声称修改后350/350与原快照相等；未做新的全量冻结、构建或dotnet测试。** runner及六个直接dotnet子任务已退出，但全机进程枚举未验证，不能推断间接子进程排空。§19.22历史3613/337不是此轮3712/350，旧警告或失败证据也不被新终验涂改。

**剩余阻断/下一关**：还缺两台实际同LAN上产品GUI Host/Client查看用户**真实**主屏、实际WPF审批人工交互/断连与双方互联网前中后始终无影响的证据；还缺持续十分钟运行的内存增长、慢网积压丢旧与完整释放DoD，不外推真实混合DPI、所有桌面条件或互联网连续性。测试夹具loopback 2×2合成画面不能替代这些项；M5明确**进行中**。保持现网DHCP/IP/DNS/网关/路由/热点/ICS/网络类别/防火墙与系统防护不动，拒绝旧Undo、不绕桌面授权；不重跑M4、不出包、不提交本节未提交代码。

### 19.24 已有配置发现字段严格校验与重新冻结（2026-10-02，未提交工作树，M5进行中）

**发现与最小修复**：§19.23首次安装确实无`config.json`时，只公告身份且零view/control的合同不变；但**已有**配置若写了有效`allowViewing`和`requireLocalApprovalForUnknownController`、遗漏`allowDiscovery`，反序列化会取默认`true`并误公告；同名不同大小写重复字段可由后者覆盖。先仅增加隔离目录和无socket夹具的`ProductFirstInstallDiscoveryTests`/`ProductHostConfigTests`案例，在旧实现上42例中的**6个目标运行期Assert红、36绿**（`outputs/m5-host-video/20261002-discovery-field-red-{build,test}.log`和原TRX）。仅在`HostRuntimeService.LoadVerifiedConfigAsync`中用已有`ReadSingleSafetyBoolean`收窄`allowDiscovery`为**必须存在、唯一、布尔值**，再与`AppConfig.AllowDiscovery`核对；已有配置缺失、重复、字符串、null均拒公告/拒监听，明确`false`保持禁发现，真实缺文件才走身份公告独立入口。原目标重建零警告错误，42/42绿（`20261002-discovery-field-green-{build,test}.log`和TRX）。未改系统网卡/地址、防火墙、默认用户秘密或M4。

**Last code commit：`b3e1273`（此前已提交的Transport基线；本节三个产品/测试文件仍在未提交工作树）。Working tree at validation：`outputs/m5-host-video/final-discovery-strict-20261002-103748-589902100/summary.json`（status=passed、exit=0、`summary.sha256`）对应上述修正后的未提交源码/测试。** 独立六阶段顺序执行Debug/Release solution与IsolatedAcceptance共四次完整Rebuild和两次solution `--no-build`测试，六原命令rc均0，四Build均**0警告/0错误**。16份原TRX逐`UnitTestResult`与Counters相符：**Debug 3721/3721、Release 3721/3721 PASS，0失败/0跳过**；各配置Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance478/Capture157/Sessions220。350项（含未跟踪Services两文件与Acceptance七文件、`HANDOFF.md`）在冻结前后及六阶段12边界逐文件SHA一致，合成SHA-256为`891220822d0bb6b731279b001d24c37578eb59f9a08c142c33cb3dfc3b575546`；Git状态原字节在冻结前后相同，摘要SHA `3641322e0d280b8f33fea1469694ef803ab3009ecdb5dda85b9f5018c93a3f92`。六原日志与证据有SHA记录；只证六直接dotnet子进程已逐个wait和runner返回，未枚举全机或证明间接子进程排空。旧§19.23的3712/350冻结仍是**修复前历史**，不覆盖、不反填为最新；本段编辑`HANDOFF.md`又成为冻结清单中的明示文档例外，编辑后不声称当前350项仍同SHA或另做全量测试。

**当时的人工停点（2026-10-02，M5未完成）**：本机真实TLS loopback在手动测试gate下把合成2×2 JPEG呈现到离屏WPF窗口，不能替代两台实际同LAN产品GUI采集真实用户主屏、真实WPF人工审批与断连。还须两端人工确认连接前/中/后互联网始终无影响，以及持续十分钟内存、慢网积压/DropOldest和原清理DoD；不以测试绿数或0活动连接伪造这些事实。不改现网DHCP/IP/DNS/网关/路由/热点/ICS/网络类别/防火墙/系统防护，不执行旧Undo、不绕桌面授权、不重跑M4、不出新包，等待确实需要的第二台机器及人工观察条件后继续。

### 19.25 M5 主机显式增强预览实验档（2026-10-08，未提交工作树）

**用户反馈与阶段边界**：用户本轮确认已用10月2日提供的旧产品GUI实机包完成其测试，指出画面糊、帧率低、键鼠不能操控。旧包正是`FramePipeline`固定200ms间隔（目标5 FPS）、`VideoQualitySettings(5, 0.5, 60, false)`，编码按比例降到50%、Client WPF再拉伸，故“糊/低帧”符合代码事实。不能操控不是网络故障：M5强制ViewOnly且UI的控制开关灰显；控制权限隔离、本机审批与紧急停止不得削弱，键鼠是**M7**范围而非本片。用户“测试通过”未逐项提供两端互联网前/中/后、十分钟每分钟内存/慢网时延/原资源排空数据，不能外推M5 DoD全绿，也不能把旧包当新档已验。

**最小实现及拒绝面**：沿用旧默认5FPS/50%/JPEG60且不改`config.json`，在产品主窗Host设置中新增一次性“本次增强预览：10 FPS / 75%（实验）”勾选；由本机显式“开启仅查看共享”在调用当时快照设置并经Host真实`CreateWithVideo`工厂注入每条会话的`FramePipeline`，管线只接上述默认或精确的10/0.75/60/false，不开放20/30FPS、100%或Auto（M6更完整的质量档与每会话独立调整后做）。其他不支持档位在主机获取身份、触碰网卡/监听之前直接拒绝；工厂也在调用借用的capture/encoder之前校验。捕获周期按目标FPS计算100ms/200ms，超预算保持既有“不追赶旧截止点”；raw与encoded仍各容量1–2并DropOldest、Client容量1+WPF单槽，视频认证、第二TLS、pin、serverProof和审批均未改。已在共享时切换勾选不会热改在途producer，UI提示必须先“紧急停止共享”完整排空后，再显式开启；Host已发布档位同启动尝试快照关联，重入不能静默换档。此档可能提高CPU和LAN码率，不宣称实际稳定10FPS或更清晰一定达标；也没有引入M7控制。

**验证及交接**：先新增合同测试，旧产品入口缺`useClearerPreview`时Debug编译准确以`CS1739`红（`outputs/m5-host-video/20261008-preview-red-build.log`，此为预期接口红，不冒充运行期红）。落地后生产工厂转发、10FPS手动时钟、非法FPS/scale/quality/Auto拒绝、无分享权限时不开网/不建配置且UI勾选非持久，以及旧默认设置测试都实际通过。新增Host三个非法档用例均在取用户秘密/刷新网卡前拒绝；定向`Sessions`8/8与`ProductHostConfigTests`36/36绿，`outputs/m5-host-video/20261008-preview-gate-{sessions-test,app-test}.log`。当前源码的最终串行Debug/Release完整solution Build各零warning/error，两配置各8程序集**3733 PASS/0失败/0跳过**，原日志`outputs/m5-host-video/20261008-preview-reviewed-{debug,release}-{build,test}.log`，由逐项目最终行累加，不硬编码旧3721。较早工厂定向遇夹具原fake未放行Stop而看门狗30秒红，修夹具先放行后目标6/6绿（旧日志保留）；不是生产方案红。**Last code commit：`b3e1273`（旧Transport代码提交）。Working tree at validation：上述3733/配置对应当前尚未提交App与Sessions画质新增；本轮未做四Rebuild/16TRX/SHA冻结，不曾发布新版ZIP、未做新版两台真实GUI预览对照。**

**当时的DoD与复测包（仅2026-10-08上午10FPS切片历史，非14点后的15FPS代码）**：先前`artifacts/m5-preview-20261008`独立`Release/win-x64/self-contained`发布WPF WinExe；经PE核对x64 GUI子系统，打包`outputs/m5-host-video/LanRemote-M5-增强预览-20261008-win-x64.zip`（435件、63,949,166字节，ZIP SHA256=`80df3c08fdd9ce1eb25a9b0473f0bb0d8076818e14e98c62904866b8cbc32fd5`），外置同名`.sha256.txt`、内含`START-HERE.txt`与各文件SHA清单，ZIP CRC及逐文件哈希已核。**此包绝不包含稍后新增的15FPS**，不能用旧哈希称已测新版。10月2日包原样保留；用户后来报告十分钟内存、慢网积压和双方上网逐项Pass，但尚未明确对应测试包哈希或原始采样，详§19.26。双端同哈希、现网不改且真实人工授权的约束不变，M7控制仍未启动。

### 19.26 用户实机DoD逐项确认后推进M5 15FPS档（2026-10-08，未提交工作树）

**用户验收事实及证据级别**：用户明确：“十分钟内存、慢网积压和双方始终能上网的逐项数据测试通过。可以继续下一步。”本节将这些条目**记作用户确认通过**，不是我在两台机器直接采集/复核：尚无两端原始内存分钟采样、慢网显示帧龄/丢旧时间线、过程中的双方联网时间线及运行包SHA。因此不填虚假数值，不将用户认可的旧画面或10FPS包成绩自动外推后来15FPS切片。M5原始§07“然后”仍列10/15/20/30FPS及scale/quality，M6才是多显示器、画质预设/每Session独立和自适应；故此轮未跳M6或提前开放M7键鼠。

**本轮最小生产改动**：保持旧默认5FPS/50%、先前10FPS/75%/JPEG60，新增与10FPS互斥的临时“本次流畅预览：15 FPS / 75%（实验）”Host勾选；两勾选自动互斥，本机未授权时不会启动Host，显式同时传两标志直接拒绝；不写`config.json`、不热改在途会话，B必须先紧急停止/完整排空再显式开启。`MainWindow`仅传点击时快照，`MainViewModel`据选择单值映射冻结`VideoQualitySettings`、UI显式报告实际已发布档；`HostRuntimeService`和`FramePipeline`/工厂将白名单扩为精确三个档，拒不在名单的fps/scale/quality/Auto，取身份/密钥/网卡前拒绝Host非法档。管线按目标fps计算采集间隔并保留双级1–2拥有型DropOldest，15 FPS的手动时钟经`.NET Task.Delay(TimeProvider)`定时器毫秒粒度按66ms登记（`TimeSpan.FromSeconds(1/15)`名义66.666ms不等于fake计时器的66ms），测试以实际调度语义约束且不假称真实稳定15FPS。认证双TLS、pin、HMAC/serverProof、ViewOnly隔离、审批、紧急停止、同子网与现网不变；未引入M6多屏或M7注入。

**当时的实测验证与停点（15FPS阶段历史）**：中间15FPS用例曾因测试把名义66.666ms误当`Task.Delay`实际66ms而发生**1红/11绿**，并非生产发现问题；保留`outputs/m5-host-video/20261008-15fps-initial-sessions-test.log`，调整测试对齐实际计时器后Sessions目标12/12、App配置目标39/39绿。当时的未提交源码串行Debug/Release solution build各0警告错误与两次`--no-build`完整solution test，各8程序集**3740 PASS/0失败/0跳过**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance485/Capture157/Sessions232），原日志`outputs/m5-host-video/20261008-15fps-{debug,release}-{build,test}.log`；这是文本结果统计，**未运行四次Rebuild/16份TRX/全源码SHA冻结**，只证当时本机自动化。**Last code commit：`b3e1273`（旧Transport已提交历史）。Working tree at validation（当时）：上述3740/配置对应未提交App/Sessions 15FPS切片，不能拿来冒充后续20FPS代码。** 当时未发布/双机安装15FPS新包，不能用10FPS包验证。后续20FPS进展以§19.27为准；M5继续进行中，M6/M7未开始。

### 19.27 继续M5主机固定20FPS/75%单轴片（2026-10-08，未提交工作树）

**范围与安全边界**：保留默认5FPS/50%、实验10/15FPS/75%（均JPEG60、Auto=false），仅新增一次性的本机主机“本次快速预览：20 FPS / 75%（实验）”勾选。三个试验勾选任意置真会清其他两个，点击显式开启时再次检查多选并快照固定`VideoQualitySettings`；未授权时不创建主机/网络/配置，勾选不持久，已监听时仅显示实际已发布的5/10/15/20档并提示先紧急停止且完整排空后重新显式开启。不偷偷按`Balanced`20档作为默认、不热切现有producer。Host/FramePipeline/Factory精确增加`(20,0.75,60,false)`白名单，当前仍拒30FPS、非本片scale、非JPEG60及Auto，识别非法设置先于取身份/网卡；捕获名义间隔50ms，原始双级容量1–2拥有型DropOldest/有界客户端呈现不变。真正能否到达20FPS、CPU/内存/慢网影响均未双机实测，不能拿用户较早Pass直接外推本轮。Control/Video双TLS、pin、HMAC、DPAPI、RFC1918同子网、ViewOnly审批/紧急停止/键鼠灰显不变，未改系统网络、未动原始规格。

**先红后绿与证据**：先仅扩Sessions两目标用例，旧白名单运行时明确**2失败/15通过**，红日志`outputs/m5-host-video/20261008-20fps-red-sessions-test.log`；未洗绿历史。实现后单元/产品定向`Sessions`17/17和`ProductHostConfigTests`45/45绿（含互斥、无权限无网络、Host拒30FPS与不允许质量/缩放组合）；日志`outputs/m5-host-video/20261008-20fps-target-{sessions-test,app-test}.log`。最终当前未提交工作树Debug/Release solution Build各**0 warning/0 error**，两配置各8项目**3751 PASS/0失败/0跳过**：Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance491/Capture157/Sessions237，日志`outputs/m5-host-video/20261008-20fps-{debug,release}-{build,test}.log`，逐项目结果由本机独立统计。**Last code commit：`b3e1273`（仅旧Transport历史提交）。Working tree at validation：上述3751/配置对应未提交App/Sessions生产+测试工作树**；没有四次完整Rebuild/16TRX/源码SHA冻结，不能假称当前20FPS新包已有真实双机/十分钟/慢网验收。旧10FPS包`80df...`与15FPS前片均不含本片20FPS，不覆盖旧ZIP。此处是20FPS阶段历史；30FPS和手动画质随后的未提交实现与验证详§19.28，M6多屏/每会话画质、M7键鼠未开始；不重跑M4、不改现网、不得降低审批安全。

### 19.28 M5 手动帧率、缩放和 JPEG 质量接线（2026-10-08，未提交工作树）

**自动继续的两片**：用户要求不依赖人工的地方继续，不将M5阶段性目标绿等同最终实机DoD。先仅对30FPS/75%/JPEG60新增Sessions两目标，旧白名单2失败/19通过，原日志`outputs/m5-host-video/20261008-30fps-red-sessions-test.log`；再最小扩Host/App与FramePipeline/Factory，四个临时实验FPS勾选互斥、已监听只能先紧急停止并完整排空再显式开启，原默认5/50/60不变。30FPS目标线程池`Task.Delay(TimeProvider)`实际毫秒节拍约33ms，并非稳定实机30帧承诺。定向Sessions21/21、ProductHostConfig50/50绿，原日志`20261008-30fps-target-{sessions-test,app-test}.log`；当时双配置各8项目**3760 PASS**/两Build零warning/error，`20261008-30fps-{debug,release}-{build,test}.log`。此为下一质量切片之前的历史，不冒充当前最终源码。

接着仅补原始`02_PRODUCT_SPEC.md`手动范围：本机主机四个实验FPS勾选与默认5FPS维持，额外显式勾“本次手动缩放/JPEG质量”后可取scale 0.50/0.67/0.75/1.00、JPEG整值40..85；不勾则默认5/50/60或实验FPS固定75/60。手动范围仅启动快照，不写config、不提供M6的Auto/每会话单独选择、不热改在途Producer；单独选择下拉不授予共享。本机显式点击、磁盘权限配置复核、真实WPF本机审批、认证Control/Video独立TLS、pin/serverProof、DPAPI、RFC1918同子网和紧急停止均不放松。FramePipeline公开只读`ValidateSettings`让Host在取身份/网卡前复用同一合法性谓词；Factory构造前也拒，绝不Clamp非法/NaN/Infinity/Auto到Balanced。WPF JPEG编码器本来支持floor缩放和JPEG40..85，输出仍被**32MiB仅压缩负载上限**限住，超过拒帧不等于可无限解码；对新100%及高质量组合把Client原16,777,216解码像素预算提至`FrameLimits.MaxDecodedPixels`单一常量，GDI在建DC前及会话工厂主屏枚举后、建encoder前拒超过预算的高分辨率主屏，拒时原capture依赖回收、未采集大帧。16,777,216像素预算不代表物理RAM多份副本可清零或所有主屏实测；过大屏目前**明确拒绝而非静默缩小**，是兼容性限制。

**证据与停点**：旧白名单手动5组测试目标**5红/0绿**，`outputs/m5-host-video/20261008-manual-quality-red-test.log`；实现后Sessions手动+拒非法+像素预算目标16/16、Capture真实JPEG各scale/质量与合成GDI边界18/18、App Host配置57/57绿，`20261008-manual-budget-{sessions-test,capture-test,app-test}.log`。首次App非法手动画质夹具曾错误要求`LoadAsync()`后没有DPAPI secrets（事实上首装Load已创建身份），5目标失败；将期望修为原身份文件字节不变后57绿，旧失败原日志保留。最终当前未提交工作树Debug/Release方案Build各**0 warning/0 error**，8项目各**3772 PASS/0 FAIL/0 SKIP**：Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance503/Capture159/Sessions244，原日志`outputs/m5-host-video/20261008-quality-{debug,release}-{build,test}.log`。**Last code commit：`b3e1273`（旧Transport历史）。Working tree at validation：3772/配置对应未提交App/Core/Capture/Sessions与测试；此轮只两次solution Build、无四完整Rebuild/16份TRX/源码SHA冻结，未制作或两机安装这版新包，不将用户此前10分钟内存/慢网积压/两端持续可上网确认外推至当前30FPS/100%/JPEG85。**

**该阶段的停点（v3冻结/新包之前）**：M5手动范围源码阶段接线已自动化通过，产品新版双机真实主屏画质与帧率、最坏手动组合下十分钟内存/自然慢网和双方互联网连续性的逐项证据未有；随后独立冻结与新哈希物料详§19.29。不能为追求无人值守放松审批、网配及上网边界。旧10FPS ZIP不含15/20/30FPS及本轮手动画质，不覆盖旧包、不拿旧包替新包验。

### 19.29 独立冻结中发现停机竞态并生成M5新哈希候选（2026-10-08，未提交工作树）

**不能洗绿的两次失败**：复制原已冻结验收器到`outputs/m5-host-video/final-m5-quality-20261008-v1/`独立运行，四完整Rebuild全部0警告错误、350项源/文档/Git状态12边界不变；Debug八项目3772 PASS；Release真实`TransportHostTests.Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission`在pending/首次accept与Stop竞速中报告`InvalidOperationException: Not listening`，Release 3771绿/1红，`summary.json status=failed exit1`。原测试单独重跑1绿只说明时序性，不算修复。第一次候选在Stop与首次Accept间短锁后、又用`listener.Server.IsBound`判已关闭socket，第二轮独立`final-m5-quality-20261008-v2/`的Debug中实际`ControlClientConnectorTests.Duplicate_Challenge_Unknown_And_Malformed_Replies_Fail_Closed(unknown,pending:true,...)`释放主机时报`SocketException: OperationAborted`，Debug 3771绿/1红，冻结源仍未漂移，v2失败保留。

**最小止血**：`TransportHost.AcceptLoopAsync`与`StopCoreAsync`只用一把短锁阻止Listener停止抢在首次`AcceptTcpClientAsync`之前，原`ValueTask`在锁外等待；停机accept遇**停机已请求且**SocketError仅为OperationAborted/Interrupted/NotSocket时当预期关闭；其它SocketException及任意InvalidOperationException仍保留给聚合错误和Dispose诊断，不再靠`IsBound`可变状态当根证据。没有长时间持锁、没把TLS/auth/审批错误洗绿或修改系统网络。现有Host 13个目标与连端相关共23个定向绿，最新v3全解双配置见下。没有额外针对每一SocketError分支的人工调度单点变异，所以不冒称彻底覆盖所有Windows竞速组合。

**最终独立冻结与边界**：`outputs/m5-host-video/final-m5-quality-20261008-v3/summary.json`真实status=passed/exit0、`summary.sha256=fc57ba7fb50d8ac65b8658c043e7e86d297da3ab98a3081b98c901554841455c`；350项源/测试/工具/脚本+根HANDOFF实际清单前后与6阶段12边界逐项SHA恒定`dbff72f66cbbdabe6ea44c5d260df3ac3f8d079fa8b301b471d99c8ffd0329d5`，HANDOFF/docs/.workbuddy memory与Git status原字节前后一致；六原dotnet子命令均rc0且wait，Debug/Release的solution与IsolatedAcceptance**共四次完整Rebuild**均零warning/error，16TRX逐项/Counters Debug和Release各八项目**3772 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance503/Capture159/Sessions244）。摘要SHA与TRX由另一独立命令核对；只证明runner直接dotnet子进程结束，不枚举全机或隐含子进程。**Last code commit：`b3e1273`（Transport旧历史）；Working tree at validation：当前M5手动画质/主屏预算/Host accept停机修复及产品App仍未提交**。本段编辑HANDOFF属于冻结清单文档例外，不称现时350项仍与冻结全同。v1/v2失败记录不重写。

**新候选与真实停点**：用v3冻结同源350项字节核对后，单独`Release/win-x64/self-contained`发布到`artifacts/m5-manual-quality-20261008/`；PE x64 GUI子系统（可双击WinExe，不是旧M4工具/脚本），独立生成`outputs/m5-host-video/LanRemote-M5-完整手动画质-20261008-win-x64.zip`，435件/63,951,214字节，ZIP SHA256=`0137f49542e0f79d43ed3022737a4eeb00653875d8677508ca1411601a8c9945`，外置同名`.sha256.txt`、内置`START-HERE.txt`和`SHA256-FILES.txt`逐文件清单；全部ZIP CRC/逐项SHA通过，排除个人config/secrets/log、脚本/PDB，不改或覆盖任何旧包。另附`M5-手动画质双机复测说明-20261008.txt`，明确两台**相同新哈希**机器上真实人工审批/上网全程、5FPS基线及30FPS+100%/JPEG85压力档十分钟逐点内存和自然慢网追新；高于16,777,216像素主屏拒绝，不要求用户改分辨率或防护。本包**未在两台机实际运行**，不把旧用户Pass外推新版，M5产品新版DoD尚待人工条件；在此期间可独立开发M6本地多屏只读枚举，但不能借此宣称M6屏切换/鼠标坐标DoD完成或开启M7控制。

### 19.30 M6 本机显示设备只读目录最小切片（2026-10-09，未提交工作树）

**接手复核而非记忆推断**：连读顶部/§1/§19.23–29、ADR045/067–073和原规格M5/M6任务及DoD；实际git状态/最近提交/diff含未跟踪App Services与产品测试核查后，接手前无M6枚举/远端metadata/切屏实现。`b3e1273`仍是最近代码提交（后有文档提交），未提交产品成果保护，未reset/checkout/clean/提交/推送。既有M5 v3原16TRX只读累加各3772 PASS，摘要SHA及完整手动画质ZIP SHA与§19.29相符，接手时350项原清单只有HANDOFF变化；未重跑旧冻结。顶部旧“不称已出包/用户明细尚未得到/M6未启动”已按最新范围纠正，不否定§19.26用户旧轮逐项Pass。

**本片实现（ADR-074）**：新增`src/LanRemote.Capture/GdiDisplayCatalog.cs`，复用只读`EnumDisplayDevicesW`/当前模式API；`GdiNative.cs`仅拆出二方法`IGdiDisplayNative`供目录借用，原native调用不变；`DisplayInfo.cs`只修ID注释（原始枚举索引，非HMONITOR/持久身份）；新增`GdiDisplayCatalogTests.cs`。目录过滤未附着/镜像驱动，保留系统顺序、原始索引、友好名回退、主屏标记、物理负坐标及竖屏尺寸。两遍完整附着设备身份与核心模式（含panning）一致才交付只读列表；设备名非空/唯一、恰一主屏、模式几何字段齐全，正宽高与right/bottom须可用int表示。每遍含被过滤项在内最多256原始条目，index256只探测结束，超限拒绝不截断，无自动重试。元数据大屏可列出，不分配DC/像素、不改变DPI/权限/分辨率，不提高M5帧预算，不暗授次屏采集。

**保留的生产边界**：原`GdiScreenCaptureBackend.GetDisplays/CaptureAsync`仍只主屏，Session factory仍`Single(IsPrimary)`；新目录不接App/Session/Control/Video消息。当前认证Control单读monitor仍把额外数据作为协议错，不能为了下一步直接并读或移除守卫；原LRVF头也没有显示器拓扑/切换关联。TLS/pin/HMAC/serverProof/DPAPI/RFC1918同子网/真实本机审批/ViewOnly/紧急停止/队列有界及config严格发现合同均未改。未动网络、防火墙、防护、旧Undo、原始规格或旧ZIP。

**真实红测与修正**：`outputs/m6-displays-20261009/initial/`新增80项首跑build rc0、test rc1，**77绿/3红**；两项真实生产缺口是第二遍`PanningWidth/Height`变化未比较，一项为fake按名称First误命中同名镜像条目的无效mode。分别补完整比较和修正fake只选择附着非镜像设备；旧红TRX/log/summary保留，不作为变异kill或洗成绿。`corrected-capture/`原命令均rc0、build0警告错误、Capture全量**239/239 PASS**。

**新独立冻结与交叉复核**：`outputs/m6-displays-20261009/final-v1/summary.json`真实**passed/exit0**；solution/IsolatedAcceptance的Debug/Release四完整Rebuild全部0警告/0错误、六直接命令rc0/wait；16TRX逐项UnitTestResult与Counters双配置各**3852 PASS/0 FAIL/0 SKIP**，分项Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance503/Capture239/Sessions244（目录80项）。352项源/测试/工具/脚本/指定根文件含未跟踪项，前后及12边界逐SHA一致`02d3371bc2366c9f23f1772857c1fa077c6685cd8a07749f611453e28bc2a87d`，docs/memory与Git原状态冻结期间不变；摘要SHA`21c88724c953e7701a3f090897dc42042ec17f9a08b82e04e1b9b5a0b2118f0f`。另一独立命令`audit_evidence.py`实际核对全部原证据SHA、TRX/Counters/程序集覆盖、12边界和当前源码，结果`independent-audit.json` passed；相对接手旧350项仅GdiNative/DisplayInfo两文件变化，另增目录/测试两文件，其他旧产品文件原字节保持。只证六直接dotnet子进程wait，不证明全机或间接进程排空。**Last code commit：`b3e1273`；Working tree at validation：上述未提交M6本机目录叠加既有M5产品成果。** 随后HANDOFF/docs/memory是明示文档例外，不再声称现时352项全部同冻结SHA。

**可证范围与剩余**：本机真实只读测试Debug/Release都输出`OrayIddDriver Device`、index4、主屏、(0,0)、1920×1080，仅观察到一个设备；合成用例覆盖三屏负坐标/竖屏、过滤、身份/模式变化、上限、异常保留与不可变快照，绝不把单一当前桌面或合成多屏写成真实物理多屏/混合DPI。两遍一致不保证原子拓扑/ABA/返回后不热插拔，枚举FALSE也不能区分全部native失败。第257项为附着设备有正反边界测试，第257项恰为过滤项尚无独立定向或guard顺序变异，保留此覆盖限制；不以静态代码检查冒充变异实测。**评审收件勘误（本轮续作）**：上一轮实际上已收到完整只读静态评审，指出第257项为过滤条目的覆盖缺口，未报告P0/P1；后续一次网关流错误不应否定已收到的正文。该评审不包含运行测试/GUI/物理多屏或双机验收，不计作独立实测通过。本机最小切片完成，不宣称M6远端metadata、切屏、鼠标坐标/每Session质量DoD完成，M7不启动。

**交付与下一关**：本轮仅结果`outputs/m6-displays-20261009/M6-本机多屏只读切片验收-20261009.txt`及新冻结证据；未出包含M6目录的新GUI包、不交付控制台/脚本替代品。原M5 ZIP仍为§19.29 SHA`0137f49542e0f79d43ed3022737a4eeb00653875d8677508ca1411601a8c9945`，不包含目录；新版双端同哈希、真实主屏/人工审批/断连、双方互联网全程无影响、基线及30FPS+100%/JPEG85十分钟逐点内存和自然慢网追新仍待人工条件。用户旧轮Pass保留不跨版外推。后续可独立做M6远端元数据协议设计/合成测试，不能谎称整个M6已因第二台机器阻断；不无理由重复已完成冻结或M4人工验收。

### 19.31 M6 过滤超限回归与host_status纯元数据合同（2026-10-09，未提交工作树）

**范围与工作树**：按磁盘顶部/§1/§19.30、ADR074、原规格04§6/11和07的M6任务及实际源码重新定位；原未提交M5/M6成果保留，最近代码提交仍b3e1273，无提交/推送。只改既有目录测试增加四例，新增Transport `DisplayCatalogFrame.cs`及`DisplayCatalogFrameTests.cs`，未修改原始规格、主屏采集或live Control。§19.30的3852/352项是历史，不冒作新冻结。

**目录guard补强**：第257个原始条目为detached或attached+mirroring、发生于第一/第二遍，共四个新增实例；连原attached两例，正常源码6绿。把guard挪到过滤之后的真实变异build0/test1，4红2绿，四红是继续读取index257触fake范围哨兵，异常不符精确预期，原证据`outputs/m6-displays-20261009/guard-v2-*`保留。finally精确恢复生产源码SHA `a4bccbadde66aac52cdce18dd29644117d49eb5e367a3a00451de408d480dabb`，Capture全量243绿。正常生产guard本来正确，补测不是修复四个产品缺陷。

**纯合同（ADR075）**：`host_status`根恰type/protocol/seq/timestampMs/displays，条目恰id/name/x/y/width/height/isPrimary；不冒充display_changed或选择指令。256KiB载荷/256项/ID0..255唯一/恰一主屏/128UTF16名称，拒重复含转义同名、未知、null、非法整数词法/UTF8/代理项与几何溢出；long几何允许负坐标和超采集预算元数据。构造有限索引复制、只读不可变值，Serialize返回独立byte[]。第257项起始token已读，但字段/DisplayInfo构造之前拒绝；不称完全未读取其字节。只做纯解析，不承担认证、deadline、seq排序或帧坐标关联，256KiB不等于堆分配总量上限。

**目标与变异证据**：`outputs/m6-metadata-20261009/initial/`首次516绿；收紧Count变化必须拒绝及最坏writer极值测试后，三处定向变异分别最大长度1红、重复字段24红93绿、long求和改unchecked int几何5红，各build0/test1。`mutations/summary.json`记录原/变异/恢复SHA，全部恢复到 `aeec523ada052f8cc7ddedbfaf56b980d9e54d659f12030990c051d43fea45f7`；`restored/`build0/test0、516绿。原红日志/TRX保留，不编造正常元数据实现首次有bug。收到的目录/元数据静态评审未报告确认P0/P1，不计为独立运行测试。

**新冻结实绩**：`outputs/m6-metadata-20261009/final-v1/summary.json` **passed/exit0**；2026-10-09 11:37–11:44本地时间执行，四次solution/IsolatedAcceptance Debug/Release完整Rebuild均0警告0错误、六直接dotnet命令rc0且wait。16TRX逐UnitTestResult/Counters动态统计，Debug/Release各**4372 PASS/0 FAIL/0 SKIP**：Core197/Security66/Protocol303/Integration3/Transport2813/Acceptance503/Capture243/Sessions244（元数据516、目录84）。354项源码/测试/工具/脚本/指定根文件前后及12边界逐SHA一致，清单SHA `61e91e77b0b6857142c90004d7b62bd3eb51da344df8c89fd80b166a1dd49458`，摘要SHA `c7376e1ebf3af24edcc397d2d236e974c5ed89fe9eaff286a9f1918b79918498`。冻结期间docs/memory/Git status不变；另一命令`audit_final.py`核对原证据库存、TRX、程序集覆盖、当前源码及全部变异恢复，`independent-audit.json` passed。相对上一目录冻结原352清单仅HANDOFF/目录测试变化，另新增codec/测试两项，其他旧源码原字节保持；只证明六直接子进程wait，不外推全机进程排空。

**边界、勘误与下一片**：真实只读两配置仍仅OrayIddDriver Device/index4/主屏/(0,0)/1920x1080，不是物理多屏/混合DPI/GUI实测。上一轮完整静态评审实际已收到，后续网关流错误不否定正文；旧结果TXT及render脚本的错误收件句保留为历史，本轮结果明确纠正。Host审批_pendingRead是只保留计数的async包装Task<int>，局部首字节不可取；Client monitor也仍固定单字节，双方live拒额外数据，不新增并读。下一无人片先验证首字节所有权原语，再设计方向/协商/帧deadline/dispatcher和原I/O join；不能直接宣称remote metadata已传输。M6切屏/帧坐标关联/每Session质量未实现，M7不启动。旧M5 ZIP SHA仍 `0137f49542e0f79d43ed3022737a4eeb00653875d8677508ca1411601a8c9945`、63,951,214字节且不含M6，未打新GUI包，新版双机DoD待真人条件，旧轮用户Pass不质疑不跨版外推。冻结后HANDOFF/ADR/memory是文档例外；下一切片改源码后须另验，不继承本轮4372冻结到新字节。

### 19.32 M6 首字节所有权前置原语（2026-10-09，未提交工作树，独立验证完成）

**续作范围（ADR076）**：§19.31新冻结及独立核对完成后，继续无需真人的小片。新增internal `src/LanRemote.Transport/ControlReadAhead.cs`，尚无任何live生产调用；39项合成测试与完整冻结均完成。构造无IO，Start唯一worker在gate内登记；流同步前缀、AsTask及原Task await在锁外完整归worker。固定一字节、None取消令牌，无timer/队列/帧解析/seq；仅原worker退出且未Stop/未消费时TryTake一次不可变byte或EOF，0字节不等于EOF。Snapshot保存不可变终态及原异常树，Completion表示排空而非读取成功。

**停止和清理**：RequestStop只逻辑失效，可短暂竞争gate但不等待IO/回调，不关闭借出的流；owner仍须锁外关闭并await Completion。停止后的迟到字节不交付，原读故障仍保留；Take已先赢的不可变字节不能撤回。保留原Task多fault/嵌套Aggregate，不用await首异常替代所有兄弟。只有取得Task且await退出才清本方buffer；ReadAsync/AsTask抛错而未知隐藏IO的违规流不清写借出buffer、不假称已join。该组件保证自身唯一读，不强制外部其它对象不读同流；不接管Host现有只含计数的pendingRead，不证明首字节到达时间或帧deadline。

**目标验证实绩（后续完整冻结见下段）**：`outputs/m6-read-ahead-20261009/initial/`首轮build0/test0、33 PASS，但完整build日志有两处CS8619测试可空性警告，不能称此首轮零警告。补重复同一异常叶子及迟到ObjectDisposedException×none/before/race后`coverage-v2/`39 PASS，仍有相同两处CS8619。三处真实变异single-take为8红31绿、stop-publication为5红34绿、fault-siblings为6红33绿，各build0/test1，无Guard代替目标断言；原生产字节恢复SHA `f23c12393131fd2f9afb4efb03f5d810de8cda222c29ee20a118f116b3b059ac`，`restored/`39 PASS。随后只给测试两处Task.Run显式可空泛型，不改断言/生产逻辑，`clean-target/`build0/test0、39 PASS且零警告零错误。原日志/TRX/变异源码/恢复SHA全部保留，定向红不冒作正常生产缺陷。上述目标阶段尚不能沿用4372证明新源码全量验证；随后独立新冻结结果如下。只读设计和新代码静态审查未报告确认P0/P1，仅供设计核对，不等于实际运行。本片不改Host审批/Client原monitor/Control额外数据拒绝，也不发送host_status、不改LRVF、次屏或键鼠；下一live接线仍需协商/方向白名单、帧预算、原IO交接与有界发布设计。

**新完整冻结实绩**：`outputs/m6-read-ahead-20261009/final-v1/summary.json` passed/exit0，2026-10-09 12:08–12:15四Rebuild零警告错误、六命令rc0且直接子进程逐个wait，16TRX逐项/Counters核对Debug/Release各**4411 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2852/Acceptance503/Capture243/Sessions244；首字节39）。356项前后及12边界原字节一致，清单SHA `7bb79707ebccfaae914e5d0611f336d07802ebe292533e26e43d9a7122136e09`，摘要SHA `6e64943e5452707fe4785007362a80d6cebcb62e5aa2ad9d37748cc14b583d10`。独立`independent-audit.json` passed，复数原TRX、日志SHA、源码/文档边界及旧M5 ZIP；相对元数据冻结原清单仅HANDOFF变化、另增本片两个文件。冻结后HANDOFF/ADR/memory记账是文档例外，不声称当前所有文件同原SHA。

**审查边界与续作**：最终只读审查完整正文已收到，未报确认生产缺陷；重核三组目标红合计19，无Guard代替断言。反射await链依赖本轮.NET私有布局；夹具finally先开闸再Guard等待，只证本次有界故障，不保证任意永久阻塞流退出或全部清理完成。下一无人片为独立有界单帧接续：用已持有首字节读余3前缀，uint限长后单payload；暂不引入deadline/timer/协商，不接live。Host旧pendingRead仍未接管；旧M5 ZIP不变、不含M6，新版双机DoD仍待真人；M7不启动、不改系统网络/防护、不提交推送。

### 19.33 M6 有界单帧接续最小片（2026-10-09，未提交工作树，独立验证完成）

**范围（ADR077）**：§19.32首字节新冻结通过后自主继续，新增internal `ControlFrameContinuation.cs`及对应测试；独立生产Transport Debug构建已成功、零警告错误。首次目标109 PASS，增加独立小额度限长拒绝Fact后baseline 110 PASS；四处定向变异和恢复后110绿已完成（下段），新全量冻结各4521 PASS已通过（下段），4411仅上片历史。无live调用，不改原FrameReader/Host审批/Client monitor/元数据codec或M5产品。

**设计**：已持有首字节→余三字节→uint验0/超限→固定有界payload，max 1..Control1MiB，无下一帧预读、无队列/重同步/JSON/认证；调用者保证唯一读者及之前原读结束。每次借用前清旧original，取得Task无论Stop都先await；原Task多fault与停止并列保留，无Task异常标BorrowExitUnproven且不清整块当前数组（其它已安全数组可清）。TryTake一次移交可变byte[]清内部引用；Stop同gate只清静止未交付数组，不擦已移交结果、不触在途区、不关流/回调。此片完全无deadline/timer/CTS，绝不接live，不宣称首字节到达时间或有限时长退出。后续统一预算/协商/方向仍待，不改网/防护、不启动M7。

**目标与变异实绩**：`outputs/m6-frame-continuation-20261009/initial/`109 PASS，构建0警告0错误；补`TinyConfiguredLimitRejects65BytesBeforePayload`后`baseline/`110 PASS。四处真实生产变异：prefix-offset 9红0绿（余前缀offset断言）、length-limit 1红0绿（65字节/64上限错误进入payload，Assert.Single）、stale-original 15红3绿（BorrowExitUnproven被旧Task误清的状态断言先命中）、fault-siblings 8红12绿（原Aggregate退化为首IOException）。均build0/test1，原日志/TRX与变异源码/恢复SHA保留；`restored/`build0/test0、110 PASS。生产原字节SHA `2c31ee8b9e43b9f618a383b4aeef866c4017eaea0e7796897b017e7d950f21a0`，测试SHA `c84fa420c61ed2c7faed38ed57c54452bba3687245bfff3537eb30f070667c92`。这些红是定向合同验证，不是正常产品自然缺陷；未对巨量长度理论项放开上限运行。正常用例覆盖整块数组不被清写，但stale-original变异在状态断言先红，不能称已到后面的数组断言。新完整冻结结果如下。

**新完整冻结实绩**：`outputs/m6-frame-continuation-20261009/final-v1/summary.json` passed/exit0，12:37–12:44四次solution/IsolatedAcceptance完整Rebuild均0警告0错误、六直接dotnet命令逐一wait且rc0；16TRX逐项/Counters/程序集复核Debug/Release各**4521 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2962/Acceptance503/Capture243/Sessions244，单帧110）。358项前后及12阶段边界SHA一致，清单SHA `53f1814023469d3d3e6fcddeeed0b2c9b0fd6d9e86d5b6758f8aa4d473ef69b3`，摘要SHA `30defbb5fd6597005fc1c884e34a79ac6a90677432418b6fb360ae5112f51355`。独立`independent-audit.json` passed，重数原TRX、日志/源码SHA、当前文件重新枚举、文档/Git字节、四变异过滤器/恢复绑定及目标Assert消息/栈。相对上片原356清单仅HANDOFF变化，新增本片生产/测试两文件；旧M5 ZIP SHA保持0137f495…不含M6。冻结后HANDOFF/ADR/memory是文档例外，不声称当前清单仍完全相同；边界采样不是连续文件锁，只证明六直接子进程wait，非全系统排空。

**审查及下一片**：完整只读审查已收到，未报确认阻断性生产缺陷；不替代实机运行。反射await探针依赖当前.NET私有布局，Guard/finally不证明任意永久阻塞条件下无后台任务；预算只指组件自有帧数组，非全机内存/异常树上界。下一独立片为显式观察锚点的绝对预算和timer原释放所有权：不把Start/Take当首字节物理到达，不改已验首读/接续与live审批；仍须之后组合验证、唯一读写者/协商/目录关联。M7不启动，无新GUI包/提交/推送/改网，M5新版双机DoD不跨版外推。

### 19.34 M6 独立显式锚定时间预算（2026-10-09，未提交工作树，独立验证完成）

**范围（ADR078）**：§19.33完成后继续无人片，新增`ControlFrameBudget.cs`和对应测试，生产独立Debug构建0警告0错误、rc0。首次目标101 PASS但测试两处CS8619，runner exit2；仅补两处Task.Run显式可空泛型后baseline/restored均101 PASS、0警告0错误，原证据保留。新完整冻结各4622已通过（如下），4521仅前片历史。显式clock/observedAt/observedFrequency/budget，构造纯参数；固定worker锁外取时与timer操作，精确Int128累计预算/频率/倒退检测，0预算无外部操作。timer禁用创建、回调有界合并，Stop纯标记通知，Finish须原释放后末检，不把Decision当join。Error与CleanupError分槽，无Task释放异常标未知退出。

**实际目标/变异**：`outputs/m6-frame-budget-20261009/`保留initial/baseline/三mutation/restored。anchor-renewal和final-sample各1红，实际先命中Expiry中的TimeoutException为空断言（C#参数先求值，未执行后续状态断言）；original-dispose 1红，实际命中AssertAwaitAsync内Completion提前退出的Assert.False自定义消息，不是Guard超时。三次均build0/test1且0警告0错误，精确恢复生产SHA `fd6e5253038bb57a8bda1488291d3ad92fdfbeb9818aa77b4181fd8466963888`，测试SHA `a3644e1b1b117b3e982ed96bc52efeb6a1eaec5d923e062cfc06eb8b8f20e086`。构造前过期旧anchor使用独立Fact，不将构造后推进时钟场景误作该变异击杀。真实System timer MaxBudget Create/Change用例已在101目标中实际运行通过，但不等待49.7天到期。

**边界与审查**：最大有限due-time4,294,967,294ms仅原语支持上限，非产品默认帧预算；已验证System timer可Create/Change，不证明49.7天后回调精度。最后采样到gate发布非原子现实时间裁决，不替代payload/父会话提交。完整只读审查未确认阻断生产缺陷；反射await探针依赖当前.NET私有布局，合作型异步IValueTaskSource完整桥接未直接专项覆盖，三处变异不代表所有近似破坏。首读仍无观察锚点接线、接续无时间预算，真实Control仍拒额外活动；不改两读原语/认证/网络/防护，不启动M7。单帧4521/358项是前片冻结，不当本片证明。

**新完整冻结实绩**：`outputs/m6-frame-budget-20261009/final-v1/summary.json`及独立审计passed，13:07–13:13四完整Rebuild零警告错误、六命令rc0且直接子进程逐一wait，16TRX双配置各**4622 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport3063/Acceptance503/Capture243/Sessions244；预算101）。360项前后及12边界一致，清单SHA `105496df558731d53beda71fd7cfd00ffc5d051b15cb760423cbc0422923d449`、摘要SHA `c7c755481528cb19e32ac49835e8b0e63dd6461a9a3aef75f6c947e73e27f1ff`。独立核对重数原TRX、两CS8619原失败门禁/两处可空标注字节变化、三变异目标消息/栈/恢复SHA/过滤器、源码重新枚举/Git和旧M5包；相对单帧原清单仅HANDOFF变动，新增预算两个文件。冻结后HANDOFF/ADR/memory更新为文档例外，下一组合源码须另验。只证明边界快照与六直接进程wait，非全机/双机/持续文件锁。未新出包或提交推送。

### 19.35 M6 独立单帧预算组合（2026-10-09，未提交工作树，独立验证完成）

**范围（ADR079）**：§19.34新4622冻结后自主继续，新增`ControlFrameReadAttempt.cs`，借用同一流的唯一读者组合三底座，不修改原首读/接续/预算。原首读worker join后采可信本地anchor（非物理到达），EOF无timer，单帧完成后预算原释放join，再同锚点精确末检和Stop同gate发布原Task结果；指定唯一消费者接数组，重复await不生新所有权。不提供成功后任意延期兑换的TryTake。AbortRequired先拒绝通知，再等待原任务；外层独立关流并join关闭，不把通知当排空。私有clock高水位仅补跨三阶段回退检测，不包装原timer/释放。

**真实缺陷与修复**：独立生产Debug普通build曾0警告0错误通过，生产原SHA `0f810b80e99fa05c90b40a69eefdb96184551f9ca45cf8cba39dd39dfcaebbd3`。完整只读审查指出拒绝静止自有payload的清理位于可能永久阻塞的原Dispose join之后；测试交付65静态例未包含所要求的清理Fact，主线程补`RejectedOwnedPayloadIsClearedBeforePendingDisposeJoins`后实际initial为**65 PASS/1 FAIL**。失败在已证接续Take完成、Stop和owner finally真实join后，Assert.All检查8字节中7字节仍非零；不是Guard/编译失败。仅在拒绝finally、join之前清理安全自有payload并置null，保留原任务完整join及后置兜底；`cleanup-fixed/`同测试字节**66 PASS**。原生产/测试备份、失败日志和TRX保留，初轮不洗绿。

**专项与变异实绩**：`outputs/m6-frame-attempt-20261009/`的baseline/restored均**66 PASS**、0警告0错误；测试仅另将原读join变异的前置锚定改到未被变异的Budget await边，并使夹具独立join子worker。四定向变异各1红：final-sample和stop-commit首红均为AssertRejected里的Expected Rejected/Actual Frame；original-read-join在原释放放行而原读取仍pending时命中AssertAwaitAsync的Completion提前退出；abort-notification命中AbortRequired未完成的Assert.True。均非Guard、均build0/test1且零警告错误；final-sample采用不可到达的运行时delta<0条件而非if(false)，不靠不可达警告击杀。精确恢复生产SHA `da12dc685213d7b2b98f38a290ceec12ce444165915ad7e11f73f11b1f096b89`，测试SHA `850e7f8951dac7837541202db11847c7ab0686f060a8319a55aac324d6198d42`。四变异是合同破坏，不冒充四个正常生产缺陷。新完整冻结结果如下，4622仍只属于预算片。

**新完整冻结实绩**：`outputs/m6-frame-attempt-20261009/final-v1/summary.json` passed/exit0，14:13:59–14:20:35四完整Rebuild零警告错误、六直接dotnet子进程逐一wait且rc0、16TRX双配置各**4688 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport3129/Acceptance503/Capture243/Sessions244；组合66）。362项前后及12边界一致，清单SHA `d43e390e75616bd7608a2a73ceba6f26613b3824339b6bc497941cf8472f7660`，摘要SHA `12dd3fc21cd0ff411fb27960858f7a5fc7a73b64458f457dc58f8b2d480246d2`。独立审计passed：重数原TRX，绑定initial→cleanup-fixed同测试六行生产补丁与之后两处测试加强，四变异精确Fact/首断言/源行/恢复字节，源/文档重新枚举、证据库存、Git和旧M5包SHA。审计初跑曾错误要求所有Theory显示名唯一；原TRX证实长参数被xUnit截断显示而testId/executionId不同，保留审计原脚本后按真实身份核验通过，没有改测试/TRX或重跑冻结。相对预算原清单仅HANDOFF变化、新增两个组合文件，其他旧源码保持。

**边界及下一片**：完整只读审查已收到；反射await链依赖本轮.NET私有布局，全部退出证据只针对夹具已取得的原任务，不证明隐藏IO、TLS/WPF副本清零或全机退出。outputs执行脚本不属源码scope，仅final执行脚本末态库存哈希；阶段快照非持续锁，冻结后文档记账是例外。下一片先做Host既有审批→holding私有单读槽所有权，保持直接await ValueTask的async计数包装、原IsCompleted活动判据和取消令牌/启动时刻，不以底层Task或新worker替代。Client monitor不改，组合当前自启首读、不能直接接Host挂起读；未知借用不清写，真实原读退出后才讨论安全清理。未接live帧、父gate、版本方向或目录UI，不启动M7、不改现网/防护/权限，旧M5候选与双机DoD边界保持。

### 19.36 M6 Host 既有活动读单槽所有权（2026-10-09，未提交工作树，新冻结及独立核对通过）

**范围（ADR080）**：保持原审批直接await ValueTask的async包装、Task.IsCompleted活动判据、原token与Read同步前缀调用位置。以具体私有owner保留同一包装Task和一字节buffer，审批到holding同owner，不增加读者/worker/AsTask/定时器或帧接线。无审批holding仍局部创建，不写_pendingRead；Client monitor、ApprovalStopAsync优先级、auth_success写/登记/Video锚点与Router收尾顺序保持。

**当前停点（专项、四变异、新冻结与独立审计均已完成）**：已实施私有`OwnedControlActivityRead`，只允许成功完成包装Activity后清理；fault/cancel及未知借用不清写。并发清理只一次归属，不声称单个并行join就是所有擦除完成屏障。新证据独立置于`outputs/m6-host-read-owner-20261009/`，不覆盖旧组合或M5包。

- `initial`实际build0但xUnit2013一警告，runner exit2、未test/无TRX；仅数组长度断言等价改为Assert.Single后，`initial-clean`零警告错误、49项31PASS/18FAIL，失败均原Memory应0实199。旧生产SHA`ee0c2d38ad2bdc52515f41ccb9bc3c63c9f7689f4084f13292a0483512ec21ad`。
- `fixed`在两测试原字节不变时实际49/49PASS、build/test均0、零警告错误，唯一生产`ControlAuthSession.cs` SHA`6acd3c6af27f4d916d5cc20d7c204b8218ffd71ac9b76f5e1d40f1f5ed7b56b0`；原审批裁决/Router/Client不改。
- 随后新增测试helper自测：`helper-initial`实际2PASS/3FAIL（兄弟异常被首错豁免、两种来源的不同取消token被误豁免），修helper后`strengthened`54/54PASS、零警告错误。原49例断言未改；最终测试SHA`29c5f9f010d4958fd00d7e13381721ea17748278a7ef0a78028b2edf90d46edc`。所有原失败和源码备份保留。
- 原合同`regression`244/244PASS：ControlAuthSession partial（含cleanup/deadline）141、ClientControlMonitor57、ClientVideoAttachTls35、FirstFrameRouterFailure11。四处变异真实零警告构建/断言失败：join-cleanup2红1绿、holding-cleanup4红2绿、success-only12红、original-read-join12红；首失败分别原Memory0/199、0/199、199/0与join.IsCompleted提前True，非Guard或编译失败。全部恢复原生产字节，`restored`54/54PASS。
- Last code commit仍`b3e1273`；Working tree at validation为未提交Host片叠加既有M5/M6成果。专项完成后实际另跑下述完整冻结，不把§19.35的4688外推本片新字节。未提交/推送/新GUI包，未启动M7。

**完整验证（实际2026-10-09 15:00–15:07，独立final-v1）**：Solution与IsolatedAcceptance在Debug/Release各Rebuild一次，四次零警告零错误；六个直接dotnet子进程按顺序wait、rc均0。16份原TRX中每配置各4742PASS/0FAIL/0SKIP：Core197、Security66、Protocol303、Integration3、Transport3183、Acceptance503、Capture243、Sessions244，Host专项54全部进入。363项实际源scope（含HANDOFF）前后及12阶段边界一致，清单SHA`665d2fdc6316f5dc8f0216d1b493cd56c671d34ed9bd128cc89930cf6bf14eec`；摘要SHA`3e74190f1d5945c81ac1cf6318783a9ccba1c00bd83bf1081f109b53e6729864`；执行器开头、12边界、末态共14次字节绑定。只证明直接进程wait及阶段采样，不称全机/隐藏IO全部退出或连续文件锁。

**独立核对**：`independent-audit.json`实际exit0/passed，SHA`4f000f66a19f898d5e3853c3bc28849de9856a9dfdba16b22cedb4281b549c90`；审计脚本SHA`07d245e6d570be1931b6ba4e13d57ee2b0bb20737e5eaf0272ad0e31c4afaccf`。从原日志/TRX核身份、计数、同测试字节红绿、helper自测插入和catch唯一替换、变异断言与恢复字节；旧组合两文件不变，旧M5 ZIP仍`0137f49542e0f79d43ed3022737a4eeb00653875d8677508ca1411601a8c9945`。初次审计因日志正则贪婪前缀/跨行空白将18个FAIL误提为10个而exit1；保留`audit-failure-20261009-151014-467511.log`和`audit-failed-bytes-20261009-151139-068365.bin`，只修逐行fullmatch后正式通过，未改测试/TRX或重跑冻结。

**审查边界及文档例外**：独立只读审查fixed/restored未发现新增阻断，旧反射入口适配确认。source-ready/wrapper-pending时直接两个join断言、success-write两测试末态清零断言是尚未加入的非阻断补强建议，不算已验证新用例。本段、顶部和ADR/memory是审计通过后的文档记账；冻结原文档快照保留，不宣称更新后的全部源scope仍等于冻结字节。交付说明位于`outputs/m6-host-read-owner-20261009/Host读槽验收结果.txt`。

**当时下一最小片（后续已完成，见§19.37）**：私有owner成功完成后一次移交EOF/首字节，拟议`TryTakeCompleted(out byte? firstByte)`与清理共用原子夺权点；失败/取消/未知借用不能夺取或交付，EOF不得混同0x00。不在本片接帧reader、移除额外数据拒绝或修改Client。M5新版双端同哈希GUI实机仍待真人条件，M4不重做、M7不启动。

### 19.37 M6 私有Host读槽一次结果移交（2026-10-09冻结，2026-10-10独立审计与交接收口）

**最小生产边界（ADR081）**：仅`ControlAuthSession.cs`的private `OwnedControlActivityRead`新增`internal bool TryTakeCompleted(out byte? firstByte)`及注释，34行/1140字节，原CRLF保持；切除此连续新增区段后逐字节等于原生产。原可见性/字段/构造/Activity/ReadAsync/Clear、审批时序与优先级、auth_success/holding/join/Router和Client monitor均不改。src只有新方法声明、没有live调用点。Last code commit仍`b3e1273`；Working tree at validation是未提交本片叠加已有M5/M6成果。

**合同**：false/null未取得、true/null成功EOF、true/0x00有效数据。仅成功包装Task可以取结果，不重复消费底层ValueTask；非法成功计数-1/2在夺权前拒绝，保留原Clear权。与Clear共用Interlocked.Exchange原数组一次归属，先复制标量再finally清零，成功Take返回前已清理。失败Take或输家Clear不是其他赢家擦除完成屏障；测试等待全部竞争调用。Take不证明父会话仍有效、认证/方向许可或帧预算，不启动任何新读。

**真实红绿与失败留存**：证据根`outputs/m6-host-read-handoff-20261009/`。original-entry-gap仅保存原三份bin及缺入口事实，不计行为red；lazy-shell实际27PASS/19FAIL，其中18真实Assert、1同步void Fact误带Timeout配置错。原日志/TRX完整保留，仅删除入口Fact的Timeout，lazy-shell-clean28PASS/18FAIL且全部真实Assert；fixed相同测试原字节46PASS。测试包含同步/异步EOF/00/C7/FF、source ready而wrapper pending、GetResult只一次、原异常/取消token、未知迟到writer、非法计数、顺序和并发Take/Clear、合成同流第一帧接续/第二帧未消费。

| 定向变异 | FAIL / PASS | 首类真实断言 |
| --- | --- | --- |
| eof-data-zero | 4 / 4 | EOF与数据0的Assert.Equal |
| clear-before-copy | 4 / 4 | 标量复制结果Assert.Equal |
| unshared-take | 2 / 2 | 原数组归属Assert.Null |
| missing-clear | 6 / 2 | 原Memory归零Assert.Equal |
| success-only | 8 / 0 | 不应抛出原错误Assert.Null |
| claim-before-success | 2 / 0 | pending原数组仍归owner的Assert.Same |
| extra-read | 8 / 0 | 原读计数Assert.Single |
| invalid-count | 4 / 0 | 无效计数不取得Assert.False |

八项均零警告构建/build0、test1、实际Assert击杀，非保护超时/编译失败；每次等待target直接子进程后精确恢复。变异汇总all_completed/final_restored为true、error=null；回归400PASS、restored46PASS。正常生产SHA `5b570be9a94630c1902fb93dd9dd251ac29068d7156249d1c9da7309fa01c93e`，新测试SHA `6bf0b645eae88e0bcf317f14f7876d129ae48ee14dc22738f8118f881926379a`。旧ownership/cleanup测试原字节保持。

**新完整冻结**：final-v1实际日志时间2026-10-09 15:50:58–15:58:14（+08:00），四Rebuild零警告错误，六直接dotnet命令顺序wait/rc0记录。16原TRX逐项重数，Debug/Release各4788PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3229/Acceptance503/Capture243/Sessions244；新46与旧54精确进入，跨配置语义/testId多重集相同。364项实际源scope前后/12边界一致，清单SHA `fc3f584e4851237aef2e836bc593e9199a6c324ce979fd40b74453962651cadc`；摘要SHA `bcef134b56ef204bdf8c005e43d781ec7f9fd6d68b68ee2493139d119eb76a61`。前片4742及旧M5 ZIP字节保持。

**正式独立审计**：2026-10-10 11:43:01–11:43:19（+08:00）实际main与外层返回0，外层原argv/PID/wait/rc记录`audit-execution-v1.json`，执行日志及脚本原bin均保存。审计未导入runner/validator，独立解析45份原TRX/19694个唯一execution，核原日志、源/文档/执行器快照、六主要历史阶段精确集合、同测试红绿、八变异精确补丁/断言源码行/恢复、旧freeze与ZIP。原审计未绑定validator且外层Python路径自证；执行前保留cc18版原bin，补已核b4f54版validator绑定、已知托管Python及runner PID互证后正式执行。执行审计SHA `7a183eab4cc8b44d40b86d9a409755d0a6c79b9d2c1225e59787276e22f1d008`；报告SHA `9d0f754170577361fce15f76a1d3f706bbf775c8263f3e28964486dcc0afa82d`。主线程另用不导入审计器的内联解析复算16TRX/9576唯一execution及当前364源一致，实际rc0。

**证据局限**：恢复会话时旧后台句柄不存在，无法重新取得final外层终端rc；磁盘summary/runner-exit-code自报0与六直接dotnet rc记录可核，不冒充本轮观察到外层退出。此次审计外层真实rc0单独记录。final前后历史仅冻结original-entry-gap/lazy-shell/lazy-shell-clean/fixed/regression/restored；八变异与计划/执行器在审计时绑定，不在final前后覆盖。每次恢复无单独时刻bin，以恢复记录、下轮原基线及最终bin互证；不声称异常中断也保证所有子进程退出。边界采样非连续锁、直接wait非全机或隐藏I/O排空，测试不是新版双机/GUI/物理多屏或TLS/WPF副本清零证明。

**文档及下一片**：本段/顶部/ADR081与2026-10-10日志为正式审计后的文档例外，不修改旧冻结快照，不宣称更新后364项仍与旧清单相同。验收TXT为`outputs/m6-host-read-handoff-20261009/Host读槽移交验收结果.txt`。下一片核定已取得首字节与原锚定预算接续，避免自启第二读或Start重置预算；已观察过期必须不发起接续，不把取时与锁外Read描述成物理原子事件。没有提交/推送/新GUI包，旧M5 ZIP仍`0137f49542e0f79d43ed3022737a4eeb00653875d8677508ca1411601a8c9945`。真实Control协商/方向/父gate尚待开发，M5新版双机DoD待真人，M4不重做、M7不启动、不改现网/防护/桌面权限。

### 19.38 M6 外部首字节原锚定入口（2026-10-10冻结、正式独立审计及交接）

**生产边界（ADR082）**：`ControlFrameReadAttempt.cs`新增internal静态工厂`FromObservedFirstByte(Stream stream, byte firstByte, TimeProvider clock, long observedAt, long observedFrequency, TimeSpan budget, int maxBytes)`、私有record/构造链及RunAsync入口分支。外部分支不创建ControlReadAhead、不重读首字节、不重新采原锚点/频率，Snapshot.FirstRead恒null；后半复用既有Budget、Continuation、原worker join、最终采样与Stop提交gate。原自首读分支及EOF路径保留，公共局部byte?只让原入口表达EOF，外部byte的00是数据。未改三个原语、Host/Client/审批/Router/协议parser，不接live；`TryTakeCompleted`及新工厂在src只有定义。Last code commit仍`b3e1273`；本恢复段未改生产或测试。

**使用与时限合同**：caller保证原读已完整退出、同一流且唯一读者，传入同源可信TimeProvider的原观察锚点/频率；工厂只校验参数，不替caller证明来源，不接owner/Task/ValueTask或父token。构造不碰流/取时。初始化首采样/Create/Change已经观察过期时零接续I/O；Ready后任意调度间隙的物理期限不保证零I/O，最终仍以原锚点Int128末检拒绝到期或等于截止。拒绝先清已Take的静止自有payload再join，不清未知借用；AbortRequired只通知，外层负责关闭原Task并独立等待，不能回等父自身。末样本与gate不是物理原子事件，成功结果不是未来权限/时间许可。Start重复调用抛InvalidOperationException；重复await已返回Task是同一结果/数组，不能冒称Start幂等。

**同测试红绿与专项**：证据根`outputs/m6-observed-entry-20261010/`。original-entry-gap仅缺入口证据，非语义red；lazy-shell同测试3PASS/45真实断言FAIL，fixed48PASS，17 Fact+14 Theory/31 InlineData展开48项。覆盖构造零外部操作、00/高字节/单帧边界、原锚点/频率/long边界、首采样/Create/Change过期零接续、原Read/Dispose join、已取得payload清理、最终采样/Stop/重复Start拒绝及结果别名；不声称新48项直接覆盖并发Start。原测试字节不改。回归464PASS=新入口48+组合66+预算101+接续110+首读39+Host移交46+Host所有权54；restored48PASS。

| 定向变异 | 实际FAIL / PASS |
| --- | --- |
| restart-first-read | 1 / 0 |
| zero-becomes-eof | 1 / 0 |
| discard-high-byte | 2 / 0 |
| refresh-anchor | 1 / 0 |
| replace-frequency | 2 / 0 |
| skip-ready-rejection | 3 / 0 |
| final-reset-anchor | 1 / 0 |
| final-allow-equality | 1 / 0 |
| drop-original-join | 2 / 0 |
| skip-final-cleanup | 1 / 0 |

**两类工具失败都保留**：十变异均实际build0/test1，15个真实断言失败并逐项恢复。原mutate_contract执行器做到第九项后因分类器仅接受`Assert.`前缀、拒绝真实自定义Assert.False消息而外层exit2；`complete_mutations.py`只补第十项并exit0，引用原summary SHA且明确original_exit_code=2。审计以原TRX消息/堆栈对应测试原字节/原日志绑定，Guard/框架错/超时不计击杀。另首轮完整冻结final-v1外层真实exit2：验证器错误要求`lanremote.integration.tests`，真实程序集为`lanremote.integrationtests`。该轮两Debug Rebuild和一次全量test都rc0，八TRX Debug4836PASS，Release未执行；没有history-after，原summary/脚本/外层记录完整保留，不能称其完整冻结通过。修正版使用显式八程序集集合，新脚本/新final-v2，并单独冻结原失败目录before/after/facts，不删白名单或补写旧后态。

**新完整冻结**：final-v2外层2026-10-10 12:40:43–12:47:10（+08:00）实际rc0已回收，child_waited/script_unchanged均true。六dotnet命令逐个rc0/wait，solution+IsolatedAcceptance各Debug/Release四Rebuild原日志均0警告/0错误。16原TRX逐条与Counters一致，Debug/Release各4836PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3277/Acceptance503/Capture243/Sessions244。旧4788用例稳定身份/多重集加新48按程序集精确对应，9672个execution唯一。365源项前后/12命令边界一致，28文档前后及命令边界一致；源码scope包含HANDOFF、未跟踪与忽略文件，仅排除bin/obj/.git等声明目录边界。主要专项/十变异历史、旧final-v1和根脚本原字节分别绑定；本次外层动态final-v2.*不纳入根冻结，另核实际argv/PID/wait/rc。验证器SHA `24589a9b56380f73fed5e552c64d400005996838dce2a230d9990a88ea881367`。

**正式独立审计**：`audit_final_v2.py`不导入runner/validator、不跑dotnet；2026-10-10 12:52:35–12:52:37（+08:00）main及外层实际exit0，输出`independent-audit-v2.json`，不是先前仅helper的prefinal通过。独立解析54原TRX/54唯一run/24707唯一execution，核原四构建日志及额外diagnostic、命令原argv/PID/wait/rc、test日志与TRX/真实断言行、全部非生产源码及文档跨阶段一致、完整源/文档路径重新枚举、同测试红绿/变异精确补丁/恢复、旧4788及新48按程序集稳定身份相加、原两类exit2与旧ZIP。审计脚本SHA `1116c0f91d5e96f5c3a89c263ed83b42f6e3098c6384b381c08b6ca4a5dade24`。主线程另从原16TRX读取Counters并回收新冻结、正式审计外层，审计输出log与报告JSON一致。

**关键SHA256**：
- 生产`ControlFrameReadAttempt.cs`：`8bb426a63af2a202b62c6c5ca8266e9583f7318fae9ec0682671ab483ae77c95`。
- 专项`ControlFrameObservedEntryTests.cs`：`d1cab8828c0d7167cf72b0744857fe358517331b3b7f614c517189b5fcb71783`。
- 365项冻结清单：`aebc6475c4e608a265a8e76124843ec2ad32f48f2d16dfef980820bb158ce3f6`。
- 新final-v2摘要：`f5971eb33a13c6193ed5034cf1bbe7fb9794f86c384237dd1c881e213b2faabc`。
- 正式审计报告：`f544a2bd6e5db12dba1d3c9e53292315f7e17c9f8236f8b2a757c1a5c6e59302`。
- 保留失败final-v1摘要：`6c0af26b90d3dd7045eb3dcfc2e62a8af08c6c000b27cf1db8a51102d989b96b`。

**文档边界与交付**：HANDOFF/ADR082/2026-10-10日志在正式审计之后更新，属于三个既有文档例外；不修改旧快照，不再声称当前365项全同冻结SHA。交接前独占保存边界，后置检查重新枚举并只允许HANDOFF在源scope变化、上述三文档变化及新增验收TXT，原证据/脚本/旧Host移交全目录与旧M5 ZIP必须保持；检查记录在本证据根`handoff-v1/`。验收TXT为`外部首字节入口验收结果.txt`。边界快照不是连续锁，直接子进程wait不证明全机/隐藏I/O排空；反射join夹具依赖运行时布局，Guard不计语义击杀；不保证恶意或永久阻塞stream/clock/timer有限退出，不证明TLS/WPF副本清零或真实双机/GUI/物理多屏DoD。未提交/推送/新包，不改现网/防护/桌面权限。

**下一接缝仅只读定位**：当前HelloFrame严格v1无应用层能力协商，TLS NegotiatedProtocol仅TLS版本，Discovery非授权，auth transcript没有未来能力字段；Host holding仍拒额外上行，Client视频attach所启动monitor仍拒额外下行。目录保持Host→Client，候选专用Host Offer→Client Accept先处理应用版本/legacy兼容决策，之后独立纯合同验证，再谈唯一读写者/截止/父gate/live。protocol2、4KiB、字段、fallback在本节冻结时均未实施或批准，不能把现有serverProof说成已绑定协商。随后用户B及纯codec进展见§19.39，本节保留历史。新版M5双机DoD仍待真人，M6整体未完成，M4不重做、M7不启动。

### 19.39 M6 新版专用目录 Offer/Accept 纯合同（2026-10-10）

**政策和代码边界（ADR083）**：用户明确选B：仅新版双方连接，旧端明确拒绝并要求升级，不保留legacy单屏分支、不静默降级；政策已经确定，不再询问。新生产只有`src/LanRemote.Transport/DisplayCatalogAgreementFrame.cs`，新专项为`tests/LanRemote.Transport.Tests/DisplayCatalogAgreementFrameTests.cs`。Last code commit仍`b3e1273`，代码及测试未提交。本片不改现行Hello/Auth/Video/monitor/审批；尚无实际旧端拒绝、升级UI、live Offer/Accept或目录传输。

**纯合同**：固定四必填字段`type`、`applicationProfile=2`、`catalogSchema=1`、`sessionId`。Offer为`display_catalog_offer`，只Host→Client；Accept为`display_catalog_accept`，只Client→Host；方向由调用方选择解析入口。profile2为应用合同，schema1为既有目录载荷版式，不是TLS或LRVF版本。载荷1..4096字节（含JSON空白、不含长度前缀），MaxDepth1；拒未知/重复/转义同名/错大小写字段、非整数词法、非canonical非空Guid、错会话、非法UTF-8/代理、BOM/注释/尾逗号/第二值/尾垃圾。允许字段重排、合法空白及等值JSON转义。Guid token在GetString前限216字节。序列化每次新数组，解析不持有输入。输出只有SessionId/IsOffer，不是授权对象。

**明确局限**：Guid匹配仅关联，caller传入已认证上下文不是本类能验证的事实。4KiB仅纯payload入口门槛，不证明live读流预分配限长。重复合法Accept可重复解析，不是反重放；未来一次Offer/Accept/无同会话重试须真实状态机实现。现行serverProof没有绑定此合同。后续若采用proof前协商，须明确并修订当前XML已认证上下文前提，另建立V2强类型规范字节与双方proof；此时点尚未定案，不将建议写成已实现。保持目录方向、原读退出/唯一读者/原预算与真实父gate，不能把codec成功当交付资格。

**同测试红绿与恢复**：拒绝壳编译成功，red展开108项=94PASS/14真实Assert FAIL，外层exit1；仅换生产后同测试原字节green108PASS/外层0。八变异外层总执行0，每项构建0/test1并等待，均真实Assert.False失败；每项恢复bin、原TRX/日志、argv/PID/wait/rc保留。回收后当前生产/测试与green原bin逐字节一致，再实跑restored108PASS/外层0。部分错token/截断/非法UTF-8矩阵主要Offer，共享Parse，不声称两方向全部排列直接覆盖。

| 生产变异 | 实际FAIL / PASS |
| --- | --- |
| allow-old-profile | 1 / 25 |
| accept-wrong-direction | 2 / 0 |
| skip-session-binding | 2 / 0 |
| allow-duplicate | 4 / 0 |
| relax-byte-limit | 2 / 0 |
| skip-trailing-read | 8 / 0 |
| skip-required-fields | 4 / 0 |
| allow-wrong-schema | 1 / 25 |

**完整冻结**：新`outputs/m6-catalog-agreement-20261010/final-v1/`，外层14:19:38–14:27:04（+08:00）真实exit0/child_waited/script_unchanged。六dotnet命令rc0/wait，solution及IsolatedAcceptance的Debug/Release四Rebuild均零警告错误；16原TRX每配置4944PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3385/Acceptance503/Capture243/Sessions244。367源/28文档前后及12命令边界保持，green/restored/final绑定同一完整交付源码。没有另跑旧parser专门回归轮；全量精确稳定身份核旧4836+新108，不删旧测。冻结包含baseline/红绿/恢复/八变异完整目录，五根脚本及既有外层文件完整集合；final当前动态外层三项另核，原首字节目录/Host目录和M5 ZIP保持。

**独立审计**：`audit.py`不导入执行器、不跑dotnet。冻结前修订独立完整集合、TRX真实结果反推rc、固定变异方法/期望失败、原日志/命令/PID/时序/源码及文档原字节、green/restored/final完整源码绑定；只读审查重读修复后无剩余确定阻断。正式main及外层14:27:19–14:27:25实际exit0，不是只读前检替代。独立43原TRX/43唯一run/19958唯一execution，含本片27份及前片16份；原Counters/稳定身份/失败堆栈源码行与原日志一致，日志输出与报告JSON相等。完整冻结和正式审计分别核实，初始红灯未覆盖；本片没有构建/执行器失败被解释成通过。

**SHA256**：
- 生产：`687c57766ed5959b1e3e44a4b38668f277919682018f9b611e5c9b007b715224`。
- 测试：`1acce6561b1e20b4547b5ef08469f3c5e34c95d35b9b5e4440c082ea2a43c244`。
- 367项冻结清单：`567806036322f92f83ae906958a6e29a3ae81279fbfe967d8b478af6bae690aa`。
- 完整冻结摘要：`9b95ef8c41d9910dd406bf7c18e94a444ea3de67ddf7417f168e718b1ca33b39`。
- 正式审计报告：`015875406168e134466c2de334ec97a0eca41badd78f8a18d58d1e947ce0b334`。
- 冻结脚本：`f070599280de0bfdc2570b2475843a4d20e9e43d2b43e4ae12bd614261c6fa98`；审计脚本：`c8771b2e9d89c9a27d6948a193c4439460a14fa5ab05bc511328c933cd9110f2`。

**交付和下一片**：正式审计后仅HANDOFF/ADR083/2026-10-10日志作为文档例外，交接前后独立核原证据和旧ZIP；不再声称当前367源清单全同冻结SHA。新增验收`新版专用协商合同验收结果.txt`，后置检查在`handoff-v1/`。边界快照非连续锁，直接子进程wait不证明全机或隐藏I/O排空；纯codec绿不证明真实GUI/双机/物理多屏或TLS副本擦除。下一片按B设计V2认证绑定与时序、严格版本拒绝及Video父资格；不以EOF/超时断言旧端，不用升级提示掩盖pin/proof/审批失败。没有提交/推送/新GUI包、不改网/防火墙/防护/权限、不覆盖旧包；M5新版双机DoD仍待真人，M6未完成，M4不重做、M7不启动。

### 19.40 M6 proof前强类型约定绑定与V2认证纯核（2026-10-12）

**用户收口与本片范围。** 用户明确“稳定，之前的卡顿应该是误判，可告一段落继续M6了”；M5按用户确认收口，撤回的卡顿判断不继续调查、不重复要求人工验收/无必要压测。§19.39及更早的“待M5真人/待proof前后定案”均是当时历史，不是当前停点。B政策不变：新版双方-only，不保留legacy，不静默降级。最近代码提交仍`b3e1273`；本片产品/测试和此前工作树均未提交。

**生产改动。** 新增`Auth/DisplayCatalogAgreementBinding.cs`及`Auth/AuthTranscriptV2.cs`（均internal），新增`AuthTranscriptV2Tests.cs`75项与`scripts/reference/gen-auth-v2-golden-vectors.py`。既有`DisplayCatalogAgreementFrame.cs`只改XML独占注释行，移除“已认证上下文”这一错误前提；codec行为不改。Binding校验非空expectedSessionId、非空两帧、Offer/Accept正确方向、两个SessionId与expected一致；transcript再校验agreement与认证session一致。值对象可重复构建使用，不是一次性授权/反重放机制。当前`ControlClientConnector`/`ControlAuthSession`真实链仍调用V1，未接Hello2/Challenge2/live V2，也未完成实际拒旧/升级UI。

**已定顺序。** TLS+pinning→Client Control Hello2→Host auth_challenge V2→Host Offer→Client Accept→clientProof V2→真实本机审批→serverProof V2→新版父会话资格。该顺序是后续接线约束，纯核只绑定本地严格解析后的语义，不证明真实消息来源、收发顺序/新鲜性或真人审批。Offer/Accept必须消耗原绝对认证窗口，不重置deadline；目录仍只Host→Client。

**规范字节与权限。** client固定16个ASCII字段，以15个单字节NUL分隔，无尾NUL：`LANREMOTE-AUTH-V2`、sessionId、serverDeviceId、clientDeviceId、serverNonce(base64)、clientNonce(base64)、certificateSha256(大写hex)、requestedPermission、`display_catalog_offer`、`2`、`1`、sessionId、`display_catalog_accept`、`2`、`1`、sessionId。Guid小写D且非空；nonce/pin各32字节，key16字节，profile/schema invariant。grant=`LANREMOTE-GRANT-V2\0`+SHA256(完整client).大写hex+NUL+grantedPermission；clientProof=HMAC-SHA256(key,client)，serverProof=HMAC-SHA256(key,`server\0`||grant)，7字节前缀hex=73657276657200。ViewOnly请求不可授Control，Control可降ViewOnly。拒外部raw/V1 transcript导入，导出均副本；临时server message/grant清零，不承诺托管字符串/TLS/框架副本擦除。JSON属性重排/空白/等值转义的语义等价不改变proof。

**同测试红绿与原失败。** 首次red因分析器警告门禁为invalid/exit2/0TRX，保留原日志及失败套件；不能计产品红灯或mutation kill。有效red-v2同75项为67PASS/8真实Assert FAIL，仅恢复生产后green75PASS。十个定向生产变异均build0/test1且逐项wait/恢复；restored全75PASS。原红灯11份TRX共28条RunInfo Error是逐例xUnit完整FAIL回显，不是RunInfo自己带Assert/堆栈；对应Failed结果才有断言与自身方法堆栈。

| 生产变异 | 实际FAIL / PASS |
| --- | --- |
| allow-escalation | 1 / 0 |
| grant-domain-v1 | 3 / 0 |
| leak-private-array | 1 / 0 |
| skip-accept-session | 1 / 2 |
| skip-auth-session | 1 / 0 |
| skip-offer-session | 1 / 2 |
| wrong-accept-schema | 3 / 0 |
| wrong-direction | 3 / 0 |
| wrong-offer-profile | 3 / 0 |
| wrong-server-prefix | 3 / 0 |

**完整冻结。** `outputs/m6-auth-v2-20261012/final-v1/`四Rebuild（solution及IsolatedAcceptance，Debug/Release）零警告错误，六dotnet命令真实rc0/wait，16TRX双配置各5019PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3460/Acceptance503/Capture243/Sessions244。每配置旧4944稳定身份+新75完整身份精确相等，green/restored/final同371项源码清单，源码及29文档前后/命令边界保持。原进程链launcher52724→freeze39612→runner17724→dotnet，完整冻结外层真实exit0已回收，不把freeze PID错当runner。freeze前后原清单1214项均保留，final130项无缺失/额外项。本次收尾未重新跑dotnet，不将审计复算误称一次新构建。

**独立审计护栏。** 仅red-v2及十个固定mutation的精确filter、rc1且已wait允许RunInfo断言回显；整行fullmatch、完整参数化名称Counter、adapter、Assert与本方法堆栈、非空机器、带时区且落在TRX/dotnet运行窗、原日志整行计数全部绑定。其他阶段拒任何RunInfo，不靠[FAIL]子串白名单。重复/错归属关键TRX容器、ErrorInfo/Text、DLL完整起始路径与storage/codeBase、falsey非列表audit_issues均有负例；自测报告绑定当前完整方法身份。

**审计修订与实跑。** 旧60项正式自测已通过并真实wait0；保留原六产物及修复前audit/post脚本八项固定摘要，明确它不证明后续修订。补上两个确定交接缺口：报告写完后将bytes/SHA256写进终态stdout并与外层日志绑定（新自测同样绑定）；audit末尾保存完整evidence manifest，before要求原项不变，仅当前空audit log→已绑定终态及新增report/execution/wait三准确名称，history另按同一canonical比较。新68项正式自测全通过，launcher49588→selftest55140、外层真实0；正式审计launcher17896→audit15680、外层真实0。报告独立核45原TRX/45唯一run/20175唯一execution、3214历史项（含三前片目录及原M5ZIP）；22已闭合外层记录=20原执行+旧/新自测。本次audit在报告内诚实记pending outer wait，结束后由独立post解析器再核五件套/PID/时窗/报告字节并闭合，不在运行中伪称已wait。审计后独立边界检查及handoff before均真实exit0。

**固定SHA256。**
- AuthTranscriptV2：`1cc0ced4d09c25f395c55c31e9e02f85dae69632ae41ffb9ac068506381de1b9`；Binding：`1301d08404143ec0fa5a35f9412d1c27126677b3ab9b96ff6f52499336200ec4`。
- Frame：`89930dec391ffe45138662e7c4f3ae8a9c07e6544749c63729327f53088248e5`；专项：`3a45da192b1c4c6db9ba7486e3748d523a2e66beb20df1e122e1097999c93273`。
- 371源码清单：`2bb62b17e480af86fe8185c8815c8b68d915dfd6b8956a695333139295e69dd3`；final摘要：`498678a0322bd577302432e08abe477297d6a78a2688f26f74f10b347782e2c0`。
- freeze-before/after：`f83325adc169ae3b629768b020248bdf3dbec9b442b07795f251d5343d2ad5e1`；68项正式自测报告：`ea29d0a8575b48ed22b7b319dc054223df2d97d839978e28e7f1356d90978c7c`。
- 正式`audit-report-v2.json`（481826字节）：`f1c7158d15aac4da28b24f1a251dbd411ab5c21b102f39d2c1386be84b3bdd62`；audit脚本：`01fcd8b142023676aad66f95a187e7915a0ddb3ab6bcb88cc91531106fcd2fc1`。

**交接与边界。** 审计后只改HANDOFF/ADR084/本日日志/MEMORY四文档，唯一新增`M6-V2认证绑定验收结果.txt`；交接before保护1376本片原项及3214历史项，四文档原字节备份在`handoff-v2/`。after独占落盘，最终完成与否以`handoff-v2/post-handoff-check.json`实际passed为准；不是事先声称结果。文档例外意味着当前含HANDOFF的源码清单不再全同冻结SHA，不冒称更新文档后重跑测试。快照非连续锁、自报PID/摘要链非OS不可篡改审计，直接wait非全机/隐藏IO排空。本片不交新GUI包、不涉及真实双端V2/物理多屏/混合DPI验收，不等于M6完成。

**下一最小片。** 独立Hello2/Challenge2严格wire合同与版本证据测试；不全局换常量，不在V2失败时回退V1，两个Host首帧入口最终同策略；不以EOF/timeout断言旧版本，不覆盖pin/proof/密码/审批失败；新版父认证后才给Video资格。仍保护TLS/pinning/HMAC/serverProof/DPAPI/同子网/真实审批/ViewOnly/紧急停止/有界队列。M5按用户反馈收口、不重跑M4、不启动M7、不改任何现网或防护、不覆盖旧ZIP，不擅自提交/推送。

### 19.41 M6 独立Hello2/Challenge2严格报文与版本证据（2026-10-12）

**范围及现态。** 承接§19.40，仅新增`WireV2ParseStatus.cs`、`ControlHelloV2.cs`、`Auth/AuthChallengeV2.cs`、`Auth/WireV2Json.cs`和`WireV2FrameTests.cs`。本片没有修改任何既有产品/测试文件；已有未提交工作保持。新类型没有接入live：两Host入口仍调用HelloFrame，FrameWriter仍写V1，ControlAuthSession/ControlClientConnector仍使用AuthChallengeFrame，AuthProtocol常量仍1。Last code commit仍`b3e1273`，不提交/推送或发M6 GUI包。

**合同。** Hello固定type=channel_hello/channel=control/protocol=2；Challenge固定type=auth_challenge/protocol/sessionId/serverDeviceId/serverNonce/certSha256/expiresInMs七字段。入口含空白总长1..4096，MaxDepth1；未知/缺失/解码同名重复字段、错类型、嵌套、注释/BOM/尾随值、非法UTF-8/代理转义均拒。合法空白、重排、等值JSON转义允许。protocol与expiry是正Int32整数字面量，拒小数/指数/符号/0/负数/溢出。Guid小写D，nonce canonical base64与pin大写hex均解码32字节；V2两Guid非空。输入复制、Copy导出与Serialize均独立新数组，不暴露私有ReadOnlyMemory底层数组。

**版本证据。** `Invalid=0/UnsupportedVersion/LegacyV1/AcceptedV2`；必须完整结构、共同字段及尾部通过后才分类，所有非AcceptedV2令out=null。完整合法V1的canonical零Guid保留LegacyV1，与现行V1合同一致，不收紧共享CanonicalGuid；V2及未知正版本零Guid仍Invalid。UnsupportedVersion仅指当前已知完整字段形状中的其他正Int32版本，不是任意未来schema识别器。LegacyV1不证明真实旧软件/身份，不授权重连降级，也不是V1握手对象；后续消费方须先保留适用的目标device/实际TLS证书pin及proof/密码/审批检查，不能因升级提示遮掉安全失败。expiry只是提示，codec不建立或重置deadline。

**有效红绿与变异。** baseline-v2采集时五文件均不存在，371原源码、29文档、4598历史项。最初baseline缺新终态summary_record，仅保留历史；补新片runner末行绑定完整summary字节后以全新baseline-v2重采，未覆盖旧产物。93项专项同测试/同展开身份：red为84PASS/9真实Assert FAIL、build0/test1；仅恢复两处生产V2接受分支后green93PASS。没有把构建/基础设施错误当产品红灯。八定向生产变异均实际build0/test1、逐项wait并恢复，restored93PASS/外层0：

| 生产变异 | 实际FAIL / PASS |
| --- | --- |
| oversize（4096→8192） | 2 / 0 |
| duplicate（去重复检查） | 2 / 0 |
| numeric-form（错误接受小数/指数） | 5 / 13 |
| early-legacy（提前分类V1） | 6 / 0 |
| empty-id（空Guid错误归LegacyV1） | 2 / 0 |
| nonce-export（泄漏私有数组） | 1 / 0 |
| pin-export（泄漏私有数组） | 1 / 0 |
| hello-legacy-accepted（V1当V2） | 1 / 0 |

**完整冻结。** `outputs/m6-wire-v2-20261012/final-v1/`四Rebuild（solution及IsolatedAcceptance、Debug/Release）零警告错误；六dotnet命令真实wait0，16TRX每配置5112PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3553/Acceptance503/Capture243/Sessions244。原5019稳定身份+新93精确相等，双配置多重集相同；green/restored/final同376源码，前后及12命令边界源码/文档/runner一致。完整冻结launcher27484→runner8928，2026-10-12 15:00:56–15:08:32（+08:00），外层真实0已回收；本次收尾只复算证据，未重跑产品冻结。

**独立审计与交接。** 新audit.py不导入runner/CORE，不执行dotnet，独立解析原XML/日志/命令/脚本快照、失败自身Assert/RunInfo与真实时窗/机器/完整身份，重新生成固定八变异期望并核恢复。主线程在正式运行前修正审计候选的相对报告路径、字段、终态键和旁路清单接口；未放宽post护栏。正式100项审计自测（含正式入口内存正反例）及7项交接护栏均真实wait0，分别launcher55468→23360、36260→17716。正式审计launcher41868→38380，15:16:59–15:17:20（+08:00）真实wait0；独立核43原TRX/43唯一run/20574唯一execution，13个本片阶段，4598历史文件不变。100/7是审计脚本自测，不加入产品5112。

报告完整bytes/SHA256进入执行期唯一终态stdout，外层记录绑定日志；报告内嵌1118项完整evidence_boundary，包含原baseline和仍为空的audit.execution.log，不排除宽泛后缀。主线程再次核对报告/PID/时窗及终态；before实际passed，恰好新增report/execution/wait三项并空log→已绑定终态，保护1121本片原文件。正式自测报告也校验当前脚本、父子PID/实际wait及报告字节。四文档原字节备份在`handoff-v1/`；after独占落盘，最终结果以`handoff-v1/post-handoff-check.json`实际passed为准，非事先声明完成。

**固定SHA256。**
- Hello：`a3e1764e75409144fe1e473ea87ac850adca14eaa0840e7fbcbabac5a3c406b9`；Challenge：`c133cf73499426bab7f1ec505cdd58394b4c570927c263f9588725abee8cae4b`。
- WireV2Json：`0c2c30054f9affccd0e3111bdaffdfd5b7889b13a75a9b02e0018432be61a4aa`。
- 376源码清单：`bfd00f3a1a4965f9ae2ddbaa785f363fa090a37430c38b3d7bf972391ed14dd5`；final摘要：`954d0454f82a4519a81383a8e27af79ce43626bb8f29d35eae506348d6a780fd`。
- 正式审计报告（1416419字节）：`90c61a45b181f13c60438bd242b4d152d96ff9536288589753234c0e01c8f0a8`；audit脚本：`a131d9f641649341794f2e08a94f532f74f57cee774f25fede1c64000a8ff980`。
- 100项自测报告：`1f97332d1f7a21a8244901e8b40a377772a117bc9567a1937fe54d298c971854`；7项护栏报告：`602be3a3788bdce20c9c85dd47994fd8bfdb2d2e939f95d833b0a75e241928c4`。

**局限及下一片。** 93为TRX展开身份，不是内部矩阵断言总数；双零Guid叠加畸形载荷、未来public字段/返回Memory反射检查、属性名的更多非法UTF-8序列是未补的非阻断建议，不能冒称全部覆盖。文件快照不是连续锁，PID/日志摘要链不是OS不可篡改审计；dotnet命令无历史parent_pid字段，直接wait不证明间接进程/全机或隐藏IO排空。文档为冻结后例外，当前含HANDOFF的清单不再全同冻结SHA；不把纯codec与旧live全量回归称真实V2拒旧/升级UI/双机多屏或M6 DoD。下一片核两Host入口和Client的V2-only握手接缝，沿已定proof前顺序、唯一读者和原绝对预算，保持安全失败分类，认证成功才给新版Video父资格。M5已收口，不重做M4、不启动M7、不改网/防护/桌面权限、不覆盖旧ZIP。

### 19.42 M6 Challenge2身份绑定消费入口（2026-10-12）

**范围。** 仅修改产品`Auth/AuthChallengeV2.cs`，新增`ChallengeV2AdmissionTests.cs`。`ValidateForConnection(ReadOnlySpan<byte>, ConnectionIdentity)`与原`Parse`共用严格`ParseCore`，没有第二套JSON解析器。顺序为非null身份→既有TLS PinsMatch→完整payload字段/语义/尾部→冻结目标device→实际TLS presented pin→版本分类。TLS pin错保持精确基础AuthenticationException；其余固定短码为client-server-device-mismatch、client-challenge-pin-mismatch、client-upgrade-required、client-protocol-unsupported、client-challenge-invalid。畸形payload不成为身份或版本证据；合法V1零Guid历史语义保留，零device在消费入口与冻结目标不符，零session且身份匹配仍为升级要求。非V2不创建对象。无I/O、时钟、proof、审批或会话授权；未来调用方必须检查原绝对deadline，不可拿对象当一次性能力令牌。

**真实验证。** 同一31专项针对旧实现可编译，运行期31FAIL均来自反射发现入口不存在的Assert；不是31种行为分别被旧实现破坏的证明。新实现31PASS。四个真实产品变异分别移除TLS pin、device、payload pin匹配及将LegacyV1提前到身份检查前，实际PASS/FAIL依次27/4、27/4、27/4、28/3；各Rebuild0/test1并逐一恢复。恢复后31PASS，产品与green字节完全相同。覆盖含身份/版本优先级、畸形尾部、全位置截断、UTF-8/BOM/代理项、重复字段、零Guid和4095/4096边界；不把TRX展开数当内部断言总数。

**全量执行。** `outputs/m6-challenge-admission-20261012/final/`于15:45:18–15:49:35（+08:00）完成，runner6360/parent54584。solution及IsolatedAcceptance双配置四Rebuild均零警告错误；六dotnet直接子进程实际wait0，外层恢复+final任务也已回收exit0。16份原TRX每配置5143PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3584/Acceptance503/Capture243/Sessions244。独立按assembly/class/method/testName多重集核旧5112+新31=5143，Debug与Release完全相同。

**独立核验及范围。** `verify_slice.py`不导入runner、不执行dotnet，实际exit0；核八阶段原TRX/definitions/entries/counters、失败Assert/本测试堆栈、唯一run id、命令wait/PID/父PID/时窗/日志摘要、同测试字节与变异恢复。枚举355项src/tests/scripts及除HANDOFF外根文件，排除bin/obj/__pycache__；不含tools源码、docs、memory或HANDOFF。它不是§19.41的376项全源码合同，未做本片100/7脚本护栏自测、完整历史证据inventory、严格日志逐测试路径映射或post-handoff before/after。四Rebuild的执行事实与快照覆盖范围分开陈述。文件快照不是连续锁，PID/摘要链不是OS不可篡改证明，直接wait不等于全机或隐藏I/O排空。

**固定SHA256。** 正常产品8963字节：`4c66cc78cfd38ce5bb377e746744473fd388f6ebb7597de708b16d35183d0d73`；355项清单：`820dd7f54cb1edc27cdba5b16a246c07b9d741a7e2a7efbe26b3681c051ec7b8`；final摘要9055字节：`9c3321ac49d1741bdb7621fc26f00bd6300e577de494a4d139646f1aacb36691`；独立报告12469字节：`9a04b94673bab229b6e6e309a860c9b3a666bd883c43dae124804e43f4c18ab2`。收尾复核355项与报告字节一致，无理由不重跑已完成全量。

**交付与续作。** 本片证据保留于上述outputs目录（仓库忽略，不自动推送原日志/TRX），新增`M6-Challenge2身份校验验收结果.txt`；交接改HANDOFF/ADR086/当日日志/MEMORY，不改已验证产品。本片代码实际提交1d917c8（6 files/+292/−11）并普通推送成功，远端refs/heads/main与本地完整号`1d917c8d2890107a7a1988c2f63f7db8815cc0da`一致；提交时工作树干净。累计基线acec599及文档8b8ede0保留。当前两Host入口/Client仍V1，不是实际V2拒旧/升级UI/双机多屏或M6完成。真正旧Host遇Hello2可能直接EOF，不能确诊为旧版本。下一片实施完整V2-only纵切：两Host首帧、Client、原预算内Offer/Accept、两端V2 proof、真实审批、新版父资格及原Video附着；VideoHello/ACK与VIDEO-V1/LRVF保持。禁止fallback、同流多读者、绕审批/放宽时限/删除join断言，不重做M4或M5人工项、不启动M7、不改网/防护/权限、不覆盖M5ZIP。

### 19.43 M6 V2-only完整认证接线与原截止补测（2026-10-13）

**范围及代码。** 本片接续5fdfe1e文档/1d917c8代码之后的累计未提交V2接线，不回退§19.42的历史V1状态。代码/测试提交为`33aca8f0207bc09a38fe1ca70594b1c78a6ea84b`，44 files/+2545/−320；文档另行记账。两Host入口（FirstFrameRouter、ControlPreAuthSession）、handoff与Client现为完整Hello2→Challenge2→Host Offer→Client Accept→clientProof V2→真实本机审批→serverProof V2。原TLS/pin/device检查优先级保持；Host Register和Client AuthenticatedControlSession都要求同SessionId agreement，但agreement不是认证/授权令牌。仍须proof、审批、success写出及Client CommitDelivery等原门禁。VideoHello/ACK protocol1、LANREMOTE-VIDEO-V1、83字节Video transcript、第二TLS、LRVF和attach/join均不全局替换，详ADR087。

**拒旧与产品提示。** 只有完整合法的V1/不支持版本声明才形成升级/不支持短码；畸形、EOF、timeout不能确诊旧端，不fallback、不伪造Challenge1。Host的LastProtocolRejection只保留本监听轮有界单槽，两工厂同策略，独立WPF文本标明“未经身份认证”；不覆盖停止/排空状态，停止时隐藏、窗口关闭停表解绑。目标device/实际TLS pin/proof/密码/审批错误不能被版本提示遮盖。旧Host遇Hello2可能直接EOF，不能保证已发布旧UI识别新提示。

**新增补测及因果。**
- Host七项：原Run状态机反射借生产clientTranscript/_client原引用，不用Export副本、不主动Clear、不新增生产observer。成功暂停auth_success write，Run未结束、registry仍0时证明原数组清零、对象不可逆终态、局部置null；放行后核holding唯一读及真实注销/join。Denied、gate自身取消、gate fault、caller取消四失败核最外finally，保留意外异常；finally解闸并观察原读/decision/Run。另加wrong-schema和第二完整Accept占response位置，精确拒绝catalog-agreement-invalid-payload及AuthResponseFrame.RejectWrongType，key/gate/registry零、limiter不变。
- Client十四例：Offer解析后四例、公开入口Accept write/flush完成后四例、原私有NegotiateCatalogAsync六例（binding/write/flush×父/子窗口）。父起点0ms、Challenge收齐200ms、Offer收齐280ms；父截止700ms、子截止1000ms。timer未派发仍按原单调截止拒绝。binding第三次有效采样后已客观到期，第四次仅检查；删目标不删除到期事件。原私有Task隔离外层兜底，不把受控边界说成同步构造耗时测量。
- 两个真实回环TLS Router拒旧测试使用strict close：只认ReadAsync=0或SocketException根因为ConnectionReset/ConnectionAborted/Shutdown；九行纯分类覆盖nested-reset、timeout、plain IO、AccessDenied等。旧Video helper未扩大修改。

**八项实际生产变异。** 各阶段原build/test/TRX、summary及源码快照全部保留，当前生产已按原字节恢复。有效矩阵由mutations-v2前三项和mutations-v3五项组成，不把v2整体失败报告改成通过。

| 变异 | 执行数/失败数 | 实际解释 |
| --- | --- | --- |
| 删除Host成功立即Clear | 1/1 | 生产_client原数组未清零 |
| Clear后不置null | 1/1 | 原局部Assert.Null失败 |
| 删除Host最外finally Clear | 4/4 | 四失败路径原数组未清零 |
| _cleared改为false | 5/5 | 清零后计算入口未抛ObjectDisposedException |
| 删除Offer解析后末检 | 4/2 | 两个无效Offer超时优先级破坏；有效Offer仍被写前检查拒绝 |
| 删除binding后末检 | 6/2 | 两个binding分支过期仍返回agreement；write/flush有前检兜底 |
| 删除Accept完成后末检 | 4/0 | binding后检查继续拒绝，纵深仍绿，不称单点kill |
| 同删Accept与binding末检 | 4/4 | 隔离write/flush分支过期仍返回agreement |

**保留失败。** 首次host-immediate-clear测试真实1FAIL，但自定义Assert.True文案缺“Assert.”，固定runner负例分类exit2；只给文案加Assert.True标识，不改条件/预算，再建新阶段。原终态变异删除唯一赋值导致CS0649，被零警告门禁挡在测试前，无TRX，不能算运行红；改显式false后才实际5FAIL。十例中间版transport-v2的1051不冒称十四例；transport-v3为1055，文案修改后另跑恢复定向及final。原失败目录、日志、脚本不覆盖，未放宽安全断言。

**恢复与完整验证。** transport-restored-20261013为Debug定向1055PASS；acceptance-v2-20261013为134PASS，均零FAIL/SKIP、各Rebuild零警告错误、源码前后一致。之后同一外层顺序启动final-v1-20261013，10:57:53–11:02:24（+08:00），runner48924；Debug solution Rebuild→Debug IsolatedAcceptance Rebuild→Debug test→Release solution Rebuild→Release IsolatedAcceptance Rebuild→Release test，六子命令逐个wait/rc0，外层任务也实际回收exit0。四Rebuild均0警告/0错误，16原TRX每配置八项目如下；主线程读过定向四日志和final六日志，不以通知替代读取。

| 项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 197 | 197 |
| Security | 66 | 66 |
| Protocol | 303 | 303 |
| Integration | 3 | 3 |
| Transport | 3685 | 3685 |
| Acceptance | 505 | 505 |
| Capture | 243 | 243 |
| Sessions | 244 | 244 |
| 总计（FAIL/SKIP均0） | 5246 | 5246 |

**完整身份差集。** 正式核验按(assembly,class,method,display_name)的Counter比较旧5143与当前5246，Debug/Release当前集合及差集都相同：新增115实例/107唯一键、消失12实例/12键。完整逐项列表在independent-final-20261013.json的baseline_comparison两配置中，交付另含完整测试身份差集TXT；主线程已逐项阅读。新增分布为104键count1、2键count3、1键count5；长JSON参数被测试显示名截断成相同前缀，不丢multiplicity：First_Frame三种extra/重复protocol/尾随对象，Handoff三种V1/V3/尾随对象，Router五种缺protocol/V1重复/V3重复/V1尾随/V3尾随，对照当前源码均为不同输入。原TRX的testId/executionId仍独立，不能称115个唯一键。

消失12项均列明迁移：Local_Write_Cancellation两行pending写序2→3、success3→4（新增Offer写）；Frames_Before_Challenge四行expected改client-challenge-invalid；First_Frame五行旧parser码迁移为严格Hello2策略（新九行，不是五行原样保留加四行，V2现合法、V1升级/V3不支持）；Legacy_ControlPreAuth方法改为ControlOnly且改用严格Hello2拒Video。其他同名fixture也迁移V2，身份存在不证明旧V1语义原样保留；不写“旧5143全部原样保留”。

**独立核验与摘要。** verify_live.py不导入runner、不执行dotnet，主线程读脚本、亲跑一次18项篡改自测（verifier-selftest-04vnsv6p）、再正式执行final-v1-20261013→independent-final-20261013.json，实际exit0。核四Rebuild/六命令argv/PID/时窗/wait、16当前TRX与16旧TRX的Definitions/Entries/Counters/执行身份、八项目日志映射、32唯一run id/20778唯一execution id、两配置稳定身份及两次独立源码重枚举。384项含src/tests/tools/scripts及除HANDOFF外根文件，排除bin/obj/__pycache__；不含docs/memory/HANDOFF。文档在验证期间及之后编辑均属范围外。verify_mutations_20261013.py另读八项原日志/TRX/快照并核恢复，实际exit0，但该脚本没有篡改自测，不冒称与final相同自测强度。

- 固定runner：6613字节，SHA256 `69449afaea6f325eae9c6558352e8dd7a279ea548f781117bc878a2a325c9a7a`。
- final summary：10182字节，SHA256 `192cad89421c142c2852d2c77a82a6ef70aece7c317884936629c8c0e315a62a`。
- 384项source-before/after：各63373字节，SHA256 `72d19c6112c64f1b3676f88f808c23be80e89167f4cebc2a04e529502ce330cf`。
- 正式独立报告：140718字节，SHA256 `f29e54201a3415fa0c5cf4945e17c794d6bec6476515e1354875344d89073b8f`。
- 变异独立报告：4950字节，SHA256 `382edeb7f9f7056059deb9b7c48409496217517d50ac0d10ce32591fc2d5bd27`。

**交付、边界与续作。** 代码33aca8f已普通push，远端refs/heads/main实读完整号`33aca8f0207bc09a38fe1ca70594b1c78a6ea84b`一致。原证据在outputs/m6-live-v2-20261012（Git忽略，普通push不含原日志/TRX）；handoff-20261013提供本片验收TXT、完整身份差集与交付清单，独立新证据ZIP不覆盖旧M5包。源码提交前已再核384原字节与final一致；代码提交和后续文档提交分开，不amend/重写历史/跳hooks/签名/改Git配置。快照非连续锁，wait/PID非OS不可篡改或全机排空证明，未做本轮全历史审计，不沿用§19.41的100/7护栏强度。回环TLS、受控IO、离屏WPF和静态审阅不是双机人工验收；无本轮物理多屏/混合DPI/新包实机体验。M6未整体完成。下一片认证后Host→Client目录传输/唯一读者分派，之后目录UI、切屏、每会话画质；M4/M5不重验、M7不启动，不改网/防护/权限、不执行旧Undo。

**证据包收据。** 新`outputs/LanRemote-M6-V2-only-验证证据-20261013.zip`实际132729436字节，SHA256 `4eeb13b0b7ece11a2c38132974a43650d118ab6acb8b18d438fac40bb19141bf`。617原文件加1清单，共618条目，回读CRC及每项原字节SHA256全部通过，打包后384源码仍匹配final。清单114753字节，SHA256 `bb76e9a4e8887c20a511664efaf71ab2faf8e739206cbdf4d4df4dce39605ff1`。包内含本片原成功/失败、自测、旧5143基线final和384源码原字节；文档是打包时快照，不含本段与当日日志后续包收据，不将其说成最后文档提交字节。ZIP不是产品GUI包、不保证异机直接运行原脚本（保留本机绝对路径），原M5ZIP未覆盖。最终交付目录另含package-verification.json与SHA256 TXT，不重写包内清单形成自引用。

### 19.44 M6 Client目录接收与原Control唯一读者（2026-10-13）

**范围及代码。** 接续04dd220文档/33aca8f代码，`79a532c82e4e73833e9021007ef9cb9c8013bd3e`已新提交并普通推送，远端refs/heads/main实读一致；9 files/+767/−33。仅Client原monitored/public Video attach登记的monitor接目录，不给所有认证成功或旧internal/Control-only路径新增读。原首字节Task退出后串行FromObservedFirstByte、最多256KiB载荷；只收严格Host→Client host_status、目录protocol1，握手/逆向/未知/畸形仍拒绝。Host holding拒绝额外输入未变，产品Host发送尚未接。

**时限、发布与关停。** idle保留原单字节读且无timer；每帧10秒初值自本地观察首读完成后的单调锚点起算，不是物理到达。接续/解析共用原锚点和频率/高水位；解析后复核截止。internal不可变单槽替换，无队列/回调/自动选屏；父gate复核真实交付与连接，撤销清空。seq/timestamp不是反重放/拓扑/坐标授权。Abort先撤销/请求close及child stop，仍完整await原组合、原IO和timer释放；保留多异常树与cleanup根。只清静止已交付payload，不承诺未知借用或TLS副本清零。释放证据分开原操作结束与全部已拥有根释放，目录timer失败/未知借用使后者false。

**补测和保留失败。** 新文件32实例覆盖分片/多帧、256KiB边界、严格方向/类型/协议、首字节与payload超时、未派发timer、迟到发布/Stop/时钟回退、原多异常/释放失败和原Task join；另有真实回环TLS目录与独立Video并行可用正例。旧两行额外字节测试迁移为非法长度：0x00现在可能为合法前缀，补齐零长度后精确length-zero；0xFF仍立即control-unexpected-data。不能说旧5246语义全原样保留。

- v3多异常用例在正常WhenAny阶段要求原read贯穿monitor完整await链，真实失败。修为正常阶段原read→continuation直接await，失败收尾再原timer dispose→monitor/父join。
- v4真实暴露Reached早于async返回/原Task赋值竞态，Assert.IsAssignableFrom null失败；改确切句柄发布TCS，保留原await断言，不增加Guard、不将WhenAny认可为join。v5/恢复定向各125PASS。
- 原截止变异使错误目录发布并进入下一空闲读，测试只等monitor导致Guard Timeout；原runner exit2和TRX保留，不计有效断言红。测试改先等monitor或Tail.Reached两种真实进展，立即Assert.Null裁决，再等原monitor；同变异重跑expiry明确Assert.Null红，stop/regression通过。失败不是生产正常超时，也不通过改日志/分类器洗绿。

**四项有效生产变异。** 严格parsed拒绝反转9/9 Assert.IsType红；删除解析后截止3例中expiry 1项Assert.Null红；删除目录AllOwnedRootsReleased条件3/3 Assert.False红；删除已交付payload ZeroMemory 3/3 Assert.All/True红。均Rebuild0/test1、固定negative runner0并原字节恢复。前三项非截止有效证据沿用原测试字节，截止v2仅改该用例观察点，385源差集核到仅该测试文件；原总编排exit2保持。恢复v2定向125PASS后再完整final。

**完整执行与身份。** catalog-final-v1-20261013于12:18:52–12:23:13（+08:00），runner6940/parent37840；Debug solution Rebuild→Debug IsolatedAcceptance Rebuild→Debug test→Release solution Rebuild→Release IsolatedAcceptance Rebuild→Release test串行六直接子命令wait0，外层任务实收exit0。四Rebuild均0警告错误，主线程已读六原日志；16TRX双配置各5279PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3718/Acceptance505/Capture243/Sessions244。相对旧5246完整(assembly,class,method,display_name) Counter为35新增/2消失，各count1，Debug/Release完全相同；32新组件实例+1新TLS实例+2改名替换，净增33，主线程逐项读取完整差集。

**核验与固定摘要。** 固定verify_live.py核六argv/日志/PID/时窗及新旧5143的32TRX，实际exit0；另verify_catalog_delta.py独立解析新旧5246的32TRX、完整身份差集、385项两次current枚举，并绑定固定旧summary/报告/runner/verifier/ZIP整体SHA及所用20个包内旧条目，实际exit0。verify_catalog_mutations.py独立读四有效原TRX、日志/退出/源差集与恢复，实际exit0。后两脚本无篡改自测，不取代成熟核验，也不冒称旧617原文件重审或全历史审计；本轮未重跑成熟脚本18项自测。385源含src/tests/tools/scripts及根除HANDOFF，排bin/obj/__pycache__，不含docs/memory/HANDOFF。所有文件快照非持续锁，PID/wait不证明全机或隐藏IO排空。

- final summary 10334字节，SHA256 `250f205c863e14481d175a83fb554bdaff40616378d450c6ac377724bb394d4d`。
- 源清单63556字节，SHA256 `3e33ade0e585809ced12928d7a2fb32deb945f9751119ef13e2ff4b788c99036`。
- catalog-independent-final-20261013.json 173249字节，SHA256 `18b0b78e0419b7bf3299684b66b6d3c3db640bf6aa4ce2ba8337fab3eda3b40d`。
- catalog-delta-5246-20261013.json 60195字节，SHA256 `abf314f5530f13f0eab9b3f4749ac88cb2162a2b5dfd7ef78b54cb8bb85577ba`。
- catalog-mutations-independent-20261013.json 34231字节，SHA256 `a114f0dd620e26ebf2f92a5ba1281b159a1688644f69625349e7a21665ab8551`。

**交付和续作。** 本片原证据保留outputs/m6-live-v2-20261012/catalog-*，Git忽略、不自动推原日志；交付新增验收TXT与完整身份差集/三份核验报告。旧M5产品ZIP及§19.43证据ZIP不覆盖、不重打。M6未整体完成，测试侧Host写目录不等于产品Host发送；尚未接目录UI、切屏、每会话画质或M7，未进行本轮双机多屏/混合DPI人工验收。下一片产品Host初始目录发送须保持success/真实审批后、唯一writer、holding原读和不合作IO的close/join边界；不改网/防护/权限、不执行旧Undo、不重验M4/M5。

**交付包收据。** 新`outputs/LanRemote-M6-Client目录接收-验证证据-20261013.zip`为132450952字节，SHA256 `305ddffa87f282c7d5b76d622f698e001c42de73d3f753c7fc0788e9351ab175`；591载荷+1清单=592条目，含本片161原证据、旧5246 final的31文件、原核验报告/脚本、385冻结项原字节及打包时四文档/验收TXT。打包前后完整枚举等于385冻结，主线程另用只读命令完整回读592条目SHA256及CRC，实际exit0。清单288921字节，SHA256 `54ec5cb7618cf28aa0b57860209fbc5161ac4619ffce17d031abc280dfe17909`；包外核验190911字节，SHA256 `2827b4341d34e2eef9769628aaf64d3cd71ca2d96af6f0d86a7003599c5a958a`。完整差集TXT28512字节，SHA256 `68844a2136fbec01bb56be955dea6530d9e0075bab3eef1dbd9ee6f19b561b11`，双配置逐项35新增/2消失。旧V2包原SHA保持；不嵌套旧包、不重打。包中文档不含本段等后续收据，不冒称最后提交字节；旧5143目录、旧V2 ZIP和绝对路径运行环境仍是外部依赖，不是全历史自包含或新产品包。执行器托管状态completed，未展示数值退出码处不以其intended_process_exit_code代替OS记录；与主线程实际exit0回读分开记账。

### 19.45 M6 Host单次目录发送底座（2026-10-13，未live接线）

**范围及接线边界。** 从902fc7a文档/79a532c代码继续，新增`src/LanRemote.Transport/HostDisplayCatalogSendAttempt.cs`及`tests/LanRemote.Transport.Tests/HostDisplayCatalogSendAttemptTests.cs`。先完成独立internal owner，不改现有Auth/Router/App/GDI/公开factory/holding。Router先await Auth再close的现状使裸await不合作provider/写出不能保证及时关线；产品Host另片接独立关闭监督，再在success完整flush→Register→秘密清理之后调用。当前测试写入不等于产品已发送，未实现目录UI/切屏/每会话画质，不宣告M6收口。

**实现合同。** 冷构造、同gate发布固定协调/operation Task，外部同步前缀在operation worker；第三个为既有ControlFrameBudget原worker。source一次、严格不可变DTO、最多256KiB载荷、BE长度前缀、一次Write/Flush，无读/重试/close/dispose；source不带token，原IO为None，不运行任意取消回调。Abort只是外层关线通知，仍持有并完整join已取得source/write/flush/预算原任务；原异常树不Flatten，未知原Task或写借用不能作已退出/清零证明。timer创建进行中由原worker未完成表示，异常/null未交资源后才记未知根。SampleGate全段序列化频率前检/原取时/频率后检/共享高水位，Stop不依赖外部clock。source前原单调锚点覆盖序列化/IO/timer释放，每阶段及提交前Int128精确截止；Sent仅历史接受，Rejected不能回滚已写字节。坏clock/provider永阻塞、隐藏root和外层close不由本底座保证有限join或全部释放。详ADR089。

**本片实际测试。** 初次新类65PASS、Rebuild零警告错误；补充独立160字节UTF8 literal与硬编码BE前缀、256条/128字符名称/最大数值组合、跨worker预算50→操作49高水位、同步前缀及确切原Task发布/join。受控stream/clock/source，不是本底座真实TLS/两机GUI。未覆盖AsTask阻塞各细分、强制双worker同时争锁、timer.Change阻塞/失败独立组合或全部错误笛卡尔积。

**变异历史。** 首轮join删除真实await关系产生6/6明确Completion提前完成断言红，但自定义中文断言文案没有固定runner所要求的Assert.，runner/编排exit2；原失败目录完整保留并已恢复。只给两条原Assert.False消息补类型标识，不改断言逻辑或固定runner；新基线65PASS。四项v2变异join6/6、最终截止2中1、未知写借用7中3、共享高水位1/1均明确断言红，固定negative runner0，原源逐项恢复；恢复定向682PASS（新65/预算101/目录516）。高水位红是ObservationError类型缺失而AssertRejected仍通过，不称移除共享高水位必然错误Sent；最终截止变异才直接出现Sent。

**完整回归实录。** `host-send-final-20261013`已完成：2026-10-13 15:14:22–15:18:31 +08:00，四次Rebuild均零警告/零错误，六dotnet子命令及外层实际wait0；16份原TRX中Debug/Release各5344PASS/0FAIL/0SKIP（Core197/Security66/Protocol303/Integration3/Transport3783/Acceptance505/Capture243/Sessions244）。387源码项前后相同，源清单SHA `3cc23024707ab777dbaa7e790bf2c8a58765b9c21b3a824340a60634dd8566c7`，summary SHA `e3643311c1c3b58b882b68db38b2a3bee3df5dd1538483fa685846491fb24901`；launcher13284→runner38848→六子命令的原PID/时窗/wait记录保留。生产18189字节/SHA `87135abbefeb031c4e5529f2343f2d228537f12f28eded575d913656dca87697`；修订测试54041字节/SHA `caa18423473b020ff5953e0a3500e3ad68ec117cae822805f650ae1607e4c5e5`。首份独立核验`host-send-independent-20261013.json`真实exit1：原检查将所有Error RunInfo拒绝，误挡负向原TRX的xUnit逐用例[FAIL]记录；失败报告/脚本/差集保留，另版仅允许负向、整行格式/时窗合法且testName Counter与原失败行精确对应的记录，仍拒崩溃/未知Error/Warning。正式v2独立核验实际exit0：报告6510281字节，SHA `0ee718f735677e9d4dd3dac0b47af914067132fdd24bbf94cccafc67fcae76e8`；重读32个新旧full原TRX/21246唯一execution，四字段Counter双配置均5279→5344（新增65/删除0，新增唯一键65且各count1）。旧385源仅新增本片两文件变为387，无旧源删除/改字节；全阶段合计40TRX/22080唯一execution，175份证据重读保持，两次当前源枚举相同。新版RunInfo内存自测20/20，核验器自身范围未做穷举篡改；主线程读完新旧精确diff、自测/完整身份差集和正式报告关键项，提交前再次重算387源/175证据一致。源码快照非持续锁，wait/PID非OS签名，非全历史审计/全机排空或双机人工。源码/测试已新提交`637e84e9302f39b64ded3868b378f9e88ec2bbd2`（2files/+1495），普通push后实读远端refs/heads/main完整号一致；文档另行提交，不把原5279外推为本片结果。证据保留`outputs/m6-live-v2-20261012/host-send-*`，不自动推送忽略的原日志，不改网/防护/权限、不执行旧Undo、不重验M4/M5、不覆盖旧ZIP。

**交付。** `outputs/M6-Host单次目录发送底座验收结果-20261013.txt`、`M6-Host单次目录发送完整身份差集-已核验-20261013.txt`、`M6-Host单次目录发送证据包SHA256-20261013.txt`与新证据ZIP `LanRemote-M6-Host单次目录发送底座-验证证据-20261013.zip`。ZIP为132944017字节，SHA256 `6b9160cf6d59eb76c4aa515ffaefb14272af8c4a49788b51716dab7e88c1e9c1`；579载荷+1清单共580项，生成器及主线程分别完整回读逐项SHA/CRC、原输入保持通过。包含本片成功/失败原证据、两版独立核验器与20项自测、旧Client final及387源；旧Client/V2包摘要保持。源/文档是打包时快照，此后包收据记账不重打；旧更早历史/运行环境仍是外部依赖，非全历史自包含、非产品包或双机验收。

**续作补记。** 文档提交`3b570b4c8c18a07bb971e27d6175cc30d86740d8`已普通推送并实读origin/main完整号一致；进入下一片之前工作树干净、387源及175原证据重算保持、包外收据六项及旧Client/V2 ZIP保持。包内文档与当时磁盘仅HANDOFF/当日日志为打包后记账差异，不重打本包。

### 19.46 M6 Router固定关闭监督（2026-10-13，前置切片）

**范围。** 从3b570b4文档/637e84e代码继续，只修改`src/LanRemote.Transport/FirstFrameRouter.cs`并新增`tests/LanRemote.Transport.Tests/FirstFrameRouterCloseSupervisionTests.cs`。固定closeSupervisor先于原core发布，Host取消回调仅通知；core结束/早退/异常也发信号。监督取得原close任务完整await，然后原Router等待监督、原活动读/审批决定，最后完成已有lease。原Auth/首帧/holding读同步前缀不移动到新线程，不改公开factory、Registry/registration/Auth/App。详细边界ADR090；关闭Task正常完成并非物理释放成功，释放错误仍保留Host诊断。首次取时在监督前，任意不合作操作或其它Host取消回调仍可能阻塞，不宣称无限制及时关闭。

**阶段实录。** 新类17实例/8方法，受控SslStream/socket，不创建网络连接：prefix/payload原读不合作、同步读前缀原线程、未知hello早退及socket/SSL原close受闸、提前取消、重复close、物理释放错误、原非网络错误、取消后IO/ODE原分类、Hello2 challenge Write/Flush不合作。基线`host-send-router-close-baseline-20261013`实际Rebuild零警告错误、17PASS/0FAIL/0SKIP，dotnet build/test及外层wait0。两项生产变异已实际跑出明确断言失败：删Host Register通知，prefix/payload各1例在10秒看门狗内未收到关闭信号，原AssertSignalAsync的Assert.Same失败（共2/2，非Timeout异常）；删await监督，socket/SSL原Dispose仍受闸时Router提前完成，原AssertRouterPending的Assert.False失败（2/2）。两项test exit1、negative runner/launcher exit0，分别原字节恢复且全源与基线相等。恢复`host-send-router-close-restored-20261013`464PASS/0FAIL/0SKIP、Rebuild零警告错误；随后执行新full，不沿用5344冒称新片结果。

**完整回归与独立核验。** `host-send-router-close-final-20261013`在16:00:20–16:04:38 +08:00完成，launcher46204→runner13988，六命令PID43264/57252/55832/28368/38680/30944。主线程已读四原构建日志和两原测试日志、summary并逐项核六execution原件相等；四Rebuild0警告0错误、六命令及外层真实wait0。16原TRX中Debug/Release各5361PASS/0FAIL/0SKIP（Core197/Security66/Protocol303/Integration3/Transport3800/Acceptance505/Capture243/Sessions244）。388源前后相等，源清单64095字节/SHA `611a110eee11551dd1b17aff1a9640adb4c71d49d290aec8600934e3b47045f7`；summary10568字节/SHA `6054d25a590cd39830e5c2781aac889a03cb52f1632d10d796a688d343431297`。

正式只读核验`verify_router_close_20261013.py`由协作者准备、主线程复读后执行，不把协作者completed误称正式核验；实际exit0，报告`router-close-independent-20261013.json`6339161字节/SHA `c5d0141e6f11099da85c1e409da3ef3996e43787924b0df2503c404ce9a8f365`。固定脚本27367字节/SHA `a6c55d4b590cac21448afa468776f9411610dc79f72b01e0e0c783ced56ef6bd`；旧v2核验器仅作固定定义库，不执行旧main、不运行dotnet/变异。旧新full共32原TRX，四字段Counter双配置5344→5361、17新增/0删除、各count1；新类基线等于完整差集，恢复464等于原full准确filter子集。两变异精确绑定原Assert消息/完整栈/行号、生产唯一替换和反向字节恢复；36阶段原TRX/21895唯一execution累计集合不清空。126证据末次重哈希及两次当前388源枚举相同。旧387源仅Router改变并新增测试，生产25468字节/SHA `98ef57daff47fd1fa35e24dc1c468b35786137ca13743869612d36766fdb2b4d`，测试35423字节/SHA `ecab6cd6071a6a28a80acef1d1ebae0ec8b91fcb84163f65bfe97badcf094443`。本核验器未新增穷举篡改自测；快照不是连续锁、wait/PID不是OS签名、不审全历史或全机排空。

**提交。** 代码/测试新提交`e7d098b6b8a8348c31c58a99d9a4c550aca47e75`（2files/+763/−5），普通push成功并实读远端refs/heads/main完整号相同。文档另行提交，outputs原证据被忽略、不随源码push上传；未修改Git配置/历史、未跳hooks或签名。

**未实施。** 此片尚未将Host目录底座AbortRequired接入关闭信号，未自动发送目录、目录UI/切屏/会话画质或新M6产品包；未新增真实TLS/双机GUI验收，不重验M4/M5、不启动M7、不改网络/防护/权限或旧包。

**交付包收据。** 新`outputs/LanRemote-M6-Router关闭监督-验证证据-20261013.zip`132058915字节/SHA `0d611e695223101be27cb739a4677af94eb2caf6b2b79adb276ff6ec13f4dac3`；523载荷+1清单=524条目，含388源码、原full/定向/变异证据及新独立核验、打包时四文档。生成器与主线程分别完整回读逐项SHA/CRC及原输入保持通过，六项包外收据绑定全部相符；清单96759字节/SHA `f17f052e53f740233a8d94c933e2385b0b7bd66fd16fe869a6f9f999bf10034a`。另交验收TXT、完整身份差集TXT和证据包SHA256收据。旧Host/Client/V2包保持，不重打；包中文档不含本段与当日日志后续收据，不是最终文档提交原字节。提交后388源及126证据再核保持，MEMORY实测9990字符。不是产品安装包或双机验收，不保证旧历史/本机绝对路径运行环境自包含。

### 19.47 M6认证后目录生命周期（2026-10-13，内部可选接线已验证）

**当前状态。** 从已普通推送的文档`c65502abb86c5e346c636a8822d2f87cbf7bc7e2`/代码`e7d098b6b8a8348c31c58a99d9a4c550aca47e75`继续。修改ControlAuthContext、ControlAuthSession、FirstFrameRouter，新增HostAuthenticatedCatalogLifetime；两个新测试文件共9方法22实例，覆盖真实Registry视频租约完成根、跨槽同异常、原holding wrapper与精确await/WhenAll/unwrap等待链。新基线22/恢复486通过，三变异close6中3红、join6红、errors4红且逐字节恢复；四Rebuild零警告错误、双配置各5383通过，正式独立核验exit0。代码/测试新提交`800148848770f6fbc5c41a18f8aca20d9ea0550b`（6files/+1377/−10），普通push成功并实读远端refs/heads/main完整号相同。391源匹配本片新冻结；旧5361仅适用于§19.46，不外推为本片结果。文档另行提交，未改Git历史/配置、未跳hooks或签名，outputs原日志不随源码push上传。

**合同。** 详ADR091：internal默认null，Router冷绑定；success完整写/flush→Register→秘密finally→原位置holding Task→目录启动。StopRequired与Completion分离，先通知实际close及注销，再完整join原来源/IO/holding/timer/已预约Video；Dispose之后才取VideoCompletion。Sent后保持原唯一读，意外sending完成也进入停止。目录故障在旧catch之外逐槽保原树，原holding分类不扩改。新增Router internal CatalogAuthSession仅保留原Auth实例供内部诊断，不扩public入口。

**定向与变异实测。** `host-send-catalog-lifetime-baseline-20261013`原TRX 22PASS/0FAIL/0SKIP，Debug Rebuild零警告错误；测试含认证成功原Write/Flush/登记/秘密finally顺序，失败成功帧不得取来源，Sent持续holding、EOF/逆向额外数据、Host取消/目录预算通知先close和注销再join，来源IO/ODE/无取消OCE/同Host token OCE在旧catch外保留，原多根嵌套和重复位置、真实Registry视频租约撤销及原完成根、source/timer Dispose同实例两来源槽。停止六例先保存并await Auth原holding wrapper，再检查source/Write/Flush→owner的exact await/WhenAll/unwrap链；WhenAny和Entered不能替代join。

`run_catalog_lifetime_mutations_20261013.py`原脚本19149字节/SHA `c3ba74b63788945eb7839294623391923192fa1fc25d3888ca57e9b775187715`：①同时删除owner与Auth目录close通知，预算三例Assert.Same红，Host取消三例仍绿（Router原取消保护）；②仅删owner JoinAttempt、保留Auth/Router兜底，六例明确Assert.False owner提前完成红；③删Router目录错误收集，四例Assert.IsType(null)红。13个失败均为原断言/完整栈，非Guard/Timeout异常或编译失败。close红是10秒有界观察未收到受控关闭通知，不冒称无时间依赖。每项均确认本次完整build/test及外层wait后按原字节恢复，原测试未改；恢复`host-send-catalog-lifetime-restored-20261013`486PASS/0FAIL/0SKIP。391源baseline/恢复一致，源清单64638字节/SHA `735f76df3cca598e8175549aa7150659a5758b2b27d53491f2f5612ffbc49635`。执行器异常中断并缺失命令记录时的自动恢复路径未获证明；不能把本次完整成功链外推全部失败路径，新版执行器须严格确认完整两命令原wait再恢复。

**完整回归。** `host-send-catalog-lifetime-final-20261013`于17:38:45–17:43:12 +08:00完成；launcher44708→runner13896，六子命令PID21424/55400/43984/59312/36256/30964。主线程读取四原构建日志、两原测试日志及summary，将六execution原件与summary逐项相等、日志字节/SHA重算核对。四Rebuild0警告0错误，六命令及外层实际wait0；16原TRX中Debug/Release各5383PASS/0FAIL/0SKIP（Core197/Security66/Protocol303/Integration3/Transport3822/Acceptance505/Capture243/Sessions244）。391源前后相等，源清单64638字节/SHA `735f76df3cca598e8175549aa7150659a5758b2b27d53491f2f5612ffbc49635`；summary10638字节/SHA `62a715e5ddb51940e3f58d97502d19a1fcd9d88117ee9ab3d8950d997f515233`。

**正式独立核验。** 主线程核原full完成并固定summary pin后运行`verify_catalog_lifetime_20261013.py`，实际exit0；脚本75960字节/SHA `416d8480e9970e75f5a19705585a55976ce2bfb5d2db4cd2839dc79a1021f478`，报告`catalog-lifetime-independent-20261013.json`6454489字节/SHA `2f71d631e1e970e71fb544b438eeeb0b82602c1dc89e0e6f3afe042a222949e6`。旧v2核验器只作为固定定义库加载，不运行旧main、dotnet或变异编排。32个新旧full原TRX独立四字段Counter双配置5361→5383，新增22/删除0，22唯一键各count1；baseline准确等于全新增差集，恢复486准确等于full的filter子集。三变异按精确生产old/new、字节偏移/逆还原、完整原失败身份/message/stack/源行号及全层wait收据核验。全部37原TRX/22012唯一execution集合不清空，146证据末次重哈希及两次当前391源枚举相等；旧388源只变更获准三生产文件并新增三文件，无删除。主线程已读完整新增身份差集并再次重核146原证据保持；提交不修改源码字节。本核验器未新增穷举篡改自测，不能外推全历史审计；快照非连续锁、PID/wait非OS签名。

**未验证边界。** 当前新增测试属于受控SslStream/时钟与真实认证/登记逻辑，不新增网络握手；视频用例是Registry真实租约及原完成根，不是真实视频TLS子连接。受控CloseHandle原Task完成不证明socket/SSL物理释放。尚无App GDI来源装配、默认产品目录发送、目录UI/切屏/会话画质、M6产品包或双机人工验收。不重验M4/M5、不启动M7、不改网络/防护/权限或旧ZIP。

**交付包收据。** 新`outputs/LanRemote-M6-认证会话目录生命周期-验证证据-20261013.zip`132232106字节/SHA `2e9ce5424fdbb3d0f3d0912b74fef71377b41b2b3e4d8d48c66520e3042237de`；543载荷+1清单=544条目，含391源码、146观察原件及新片全部阶段、独立核验/执行脚本与打包时四文档。生成器和主线程分别完整回读准确条目集合、逐项字节/SHA/CRC、原输入保持及六项收据绑定均通过；清单144375字节/SHA `f747067c5e2fc15151468046a8268812be7f784344e281ae3161bdd5b2f624b4`。另交验收结果TXT、完整身份差集TXT、证据包SHA256收据；旧Router/Host/Client/V2四ZIP保持。提交后391源仍等于full，MEMORY9973字符。本段及当日日志收据是打包后的文档追加，不重打冻结包；不是产品安装包/双机验收或全历史运行环境自包含包。

### 19.48 M6 Host惰性只读目录来源与App默认装配（2026-10-14）

**当前状态与提交。** 承接已推送文档2310b5af604dd5604b52eab7feadc5f948a0111e/代码800148848770f6fbc5c41a18f8aca20d9ea0550b的干净工作树。两生产文件ControlAuthContext.cs、HostRuntimeService.cs及两新测试HostDisplayCatalogSourceTests.cs、ProductHostDisplayCatalogTests.cs已新提交`c745ae42c92593e937ec85d8bbd8e0899f993d01`（4files/+773/−1），普通push成功并实读远端refs/heads/main完整号一致。App默认Host已装配惰性只读GDI来源，不再只是internal opt-in；未配置的其他context仍默认null。未修改csproj、Auth/Router/Registry、Client monitor或视频管线。本节不是M6整体收口，outputs证据被忽略，不随源码push上传。

**公开合同（ADR092）。** `WithDisplayCatalogSource(Func<ValueTask<IReadOnlyList<DisplayInfo>>>, TimeSpan)`返回record副本，保持SecretLoader/store、limiter、审批门、Registry、时钟及选项的原引用；显式拒null及非法预算，配置不调用来源。非async外桥先求值`source().AsTask()`，取得原Task后才进入ProjectCatalogAsync；source/GetStatus/OnCompleted未交Task的同步失败仍为未知退出，GetResult失败但已交Task则已知退出。投影完整await原Task并从Task.Exception保兄弟/嵌套/重复引用，不Flatten、不以WaitAsync或额外Task.Run/CTS遗弃原根。单根faulted OCE经过async投影可能变Canceled，本片保证原异常实例/token和来源诊断，不保证原/投影Task状态完全等价；不能重建上游已丢失的根。

**App与消息。** StartAsync同轮context通过公开入口装配`(() => ValueTask.FromResult<IReadOnlyList<DisplayInfo>>(new GdiDisplayCatalog().GetDisplays()), TimeSpan.FromSeconds(10))`；枚举器每次独立、留在委托内，不在配置/Host构造时枚举。Transport不新增Windows依赖；公开API不暴露wire DTO/流/token。每父连接当前仅一次目录，seq=0，成功来源后取DateTimeOffset.UtcNow.ToUnixTimeMilliseconds（不是Context.TimeProvider.GetUtcNow）；UTC只是历史元数据，不作安全预算、拓扑世代或视频坐标。DisplayCatalogFrame严格校验并复制有界列表，不造主屏、不截断。10秒仍覆盖原source前单调观察→来源/投影/序列化/Write/Flush/timer释放及最终接受；沿§19.47先close/注销后join，不承诺强制中断原生GDI或有限结束。调用方须并发安全、交还覆盖全部所属工作的ValueTask，不隐匿后台工作、不并发修改已交还列表。

**定向实测。** 来源初始22例通过，补单根/多根OCE后最终`host-send-public-source-baseline-20261014`24例通过；覆盖冷配置及全部共享引用、非法目录8例、完整异常树、原source在Stop/预算后的实际await链、同步阻塞前缀、IValueTaskSource转换差异、并发独立来源。`host-send-public-product-v2-20261014`3例通过：真实App StartAsync、隔离DPAPI/证书、127.0.0.1 Control TLS/pinning/V2认证、真实WPF控件自动拒绝/批准、真实GDI目录接收。错key/待批/拒绝不调用已装配source，ViewOnly批准后恰好一次，目录后父会话保持，Client关闭而Host仍运行时自然注销/排空。反射读取真实Host closure中context，以同步Enter→Invoke(original)→原样返回ValueTask透明观察；计数从StartAsync返回后开始，不能单独证明启动期间cold。测试未attach Video，Control-only Client没有monitor，测试自己的FrameReader是唯一读者；不代表已装配App Host不会向Control-only连接发送目录。InteractiveDesktop正例需可枚举且稳定桌面，不skip、不伪造通过。

**失败保留与变异。** 初次`host-send-public-product-initial-20261014`因Expression类型歧义CS0104构建6错误/0警告，未执行测试、无TRX；仅改using别名后新label重跑通过。初始串行&&后的来源baseline未执行，不补写通过。新变异tree删除原错误树重抛，4中3断言红；prefix把外桥改async，4中3断言红（GetResult已交Task仍绿符合合同）；app删除真实装配，3断言红但均止于非null配置断言，不称三条独立线缆行为kill。9原失败message/完整stack保留，非编译失败、Guard/Timeout异常或runner崩溃。变异用正常测试模式，原build rc0/test rc1/runner及launcher实际wait2是预期失败记录，不翻为测试通过；三项按原字节恢复，恢复Transport111/产品3通过。

新`run_public_source_20261014.py`与`run_public_source_mutations_20261014.py`要求精确两命令集合、原execution/summary/PID/wait/log和目标变异字节一致才恢复；没有finally自动写回，缺收据保留可见变异。本次证明成功完整执行链；没有test启动后异常中断故障注入，没有修写旧执行器，也没有新增核验器穷举篡改自测。旧脚本缺口不冒称已全面证明。

**完整验证。** `host-send-public-final-20261014`于11:00:27–11:04:40 +08:00完成，launcher31192→runner54492。四Rebuild0警告0错误，六dotnet命令及外层均实际wait0、commands_fully_receipted=true。16原TRX双配置各5410PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3846/Acceptance508/Capture243/Sessions244。源清单393项、64996字节/SHA `ae8461489250ce2d165de54dfe8ebb071b3b9f5aef86c01b5423f87e58c195df`；summary10457字节/SHA `1234f4a187aef9b34e199686a0ee1229930277e489b05bc6dcff68480f3d33b1`。主线程读取Debug-test及summary，独立核验器读取全部原构建/测试日志和执行收据，不冒称主线程逐份显示读取了所有日志。

**独立原证据核验。** 固定full summary后实际运行`verify_public_source_20261014.py`，exit0；脚本16267字节/SHA `dd3cb8940ef15a4a2fb9717c9e99f0e013c3f877099a70e161abf1bb8144fa1d`，报告`public-source-independent-20261014.json`6447404字节/SHA `4a7678c134175034078a4584d2ffb3c1e7b50a525c6ce5c051787800d1be9335`。旧v2核验器仅作固定定义库，不运行其main、dotnet或变异。旧5383四字段完整Counter全部保留，新增27/删除0，各新增唯一身份count1，双配置Counter一致；来源24/产品3及恢复111/3精确等于full filter子集。累计39原TRX/21738唯一execution（不含初始22例，不含无TRX编译失败），393源两次枚举及168观察原件末次重哈希保持。变异原old/new字节、原断言及完整wait逐项绑定；编译别名修正独立核对。代码提交后重新完整枚举393源、重哈希168原件一致。快照非持续锁、PID/wait非OS签名，不是全历史/全机审计。

**边界与下一片。** 真实单机Control TLS/WPF控件自动操作/只读GDI不是人工审批验收、双机LAN、物理多屏、混合DPI、热插拔或目录与第二视频TLS联合测试；没有采像素、没有新M6产品安装包。Client公开只读目录投影、App目录显示/会话清理、切屏和每会话画质仍待后续。保持安全与只读现网约束，不重验M4/M5、不启动M7、不改网络/防护/权限、不执行旧Undo或覆盖旧ZIP。

**交付收据。** 新`outputs/LanRemote-M6-Host默认目录来源-验证证据-20261014.zip`132266906字节/SHA `216ea9fbc6d92933c5f07abbd71c86a899f1c9716eae361dd96b33c74cd62945`；586载荷+1清单=587条目，含393源码、168观察原件及本片初始/失败/定向/变异/恢复/full原阶段、执行/独立核验脚本及打包时四文档。清单111896字节/SHA `4e84041329c0350ec094b559a661f86cb550a876899d9ec4b801814a3d9cdf22`。生成器与主线程分别完整回读精确条目集合、逐项SHA256/CRC和原输入保持，六项包外收据绑定均通过；五旧M6证据ZIP与M5完整手动画质ZIP对照历史SHA保持。另交`M6-Host默认目录来源验收结果-20261014.txt`、`M6-Host默认目录来源完整身份差集-20261014.txt`、`M6-Host默认目录来源证据包SHA256-20261014.txt`。打包前记忆长度门禁曾拒绝执行，精简后实测9907字符再运行成功，失败尝试未生成交付物。本段及当日日志后续收据是打包后的文档追加，不重打/覆盖冻结包；包不是产品安装包或全历史自包含环境。

### 19.49 M6 Client公开只读目录与App显示/会话清理（2026-10-14）

**起点与提交。** 承接文档38b70905e6bbc25b535baad65ee77a1904481b4a/代码c745ae42c92593e937ec85d8bbd8e0899f993d01。本片6生产文件、6测试文件新提交`3a4280d0157f0a0775aaa619dae8a984ac29caff`（12files/+722/−18），普通push成功并实读远端refs/heads/main完整号一致。不改认证、原唯一读者、目录预算、Host发送与原网络关闭合同。outputs仍被忽略，源码push不上传本地证据。

**公开投影（ADR093）。** 新DisplayCatalogSnapshot为sealed、internal构造器、Sequence/TimestampMs/Displays只读；DisplayCatalogFrame完成原验证/复制后构造一次PublicSnapshot，内部属性转发，无第二可变会话槽。GetDisplayCatalogSnapshot在原父gate下仅已交付且未撤销返回；未收到完整帧为null，同一目录反复读取返回同一对象，无附加IO/attach/token/流能力。撤销不抹除调用方已保留的历史快照；seq下降也可替换，seq/timestamp不是拓扑世代、反重放、选屏授权或视频坐标关联。

**真实App接线。** ClientConnectService仍先认证再Video attach；原Dispatcher建窗块固定绑定该Attempt父会话getter及停止token，不动态查_active。RemoteViewWindow每实例一次绑定，单DispatcherTimer名义250ms采样，同实例不重复绑定ItemsSource；只读滚动区域显示ID/名称/宽高/原点/主屏标记，明确非当前视频选屏状态。无后台目录worker、事件网络读者、目录队列、选择按钮或键鼠入口。250ms不是实时刷新/撤销上限，Dispatcher阻塞与父gate竞争仍能延迟处理，不声称跨线程原子瞬时清屏。

**清理与错误。** Tick在getter前后查停止/关闭/终态，丢弃取消或重入停止后的返回值；停止后的旧Tick不再调用getter、不恢复绑定。本地Closing在请求取消前、服务在原Drain前、SessionEnded含错误保窗均清timer并退订、清getter/快照/token/ItemsSource；不新增Closed兜底。DisconnectHandlerReleased纳入这些根；UI清理异常追加而非覆盖，仍执行原四支Drain与join。原错误保窗/二次关闭、Cleanup Task身份及故障后重连政策不变，不吞OCE/IOException或洗绿Faulted任务。

**测试。** 新增24case：Transport11实际消费受控原reader，覆盖三分块、同实例不可变、未交付/半帧、三种撤销与原IO未退出、迟到帧、较低seq替换、双会话隔离；WPF10使用真实STA/Dispatcher和反射纯数据夹具，覆盖真实timer、四停止路径、前后取消/重入/重复绑定/残留证据；产品3走真实ClientConnectService、双TLS/pinning/V2/Video attach、显式测试审批gate、合成两屏及2x2 JPEG，覆盖立即/延迟目录与服务外部停止。审批前来源calls0，批准后核真实ItemsSource完整字段。慢Drain用例在ConnectAsync返回后提前登记取消阻塞，后登记的Presenter WaitAsync先收到取消；禁用UI timer退路，以原Control getter变null证明已进入Drain，再直接断言UI已清且原取消/client/Cleanup仍pending。finally释放受控闸门并等待原线程/任务。不是双机人工、真人审批、物理多屏、混合DPI/热插拔或GDI与本片双TLS联合验收。

**失败历史。** Transport initial100中1红源于误用AssertRevokedWithoutClose；原monitor可并发请求close，改AssertRevoked后v2为100绿，不改生产关闭行为。产品v2的20中1红因阻塞取消回调注册太晚，连原WaitAsync取消通知也阻塞；提前登记后v3为20绿。首次变异前五项执行并恢复，第五App仅Guard等不到目录，执行器拒绝汇总、未执行第六，不记正式kill；保留执行器原副本。补真实建窗后_readCatalog非空直接断言及进入Drain后的Null断言，v4为20绿。首轮full Debug5432PASS/2FAIL：两旧公共面精确白名单漏列新getter，Release未开始；仅各加入GetDisplayCatalogSnapshot，保留其他公共面禁流/token/预算构造器规则。旧失败阶段从不覆盖或改写通过。

**正式六变异及恢复。** 新-v2阶段：getter永null3红、删除取值后停止检查2红、不启动timer1红、不清ItemsSource4红、删除App绑定3红、删除Drain前清理1红；全为原Assert失败，非编译/Guard/Timeout/执行器崩溃。App三红止于非空绑定断言，不夸为三条独立线缆kill；慢Drain原Assert.Null红。各原build/test、launcher/outer实际wait及原日志收据核验完整后恢复单点生产字节，无finally盲恢复；未做异常中断故障注入。v4恢复Transport616/产品20通过；两白名单修订后新恢复Transport656/产品20通过。独立核对两份最终测试去掉唯一新增getter字面量即精确还原v4 SHA，其他397项不变；不将旧变异源码冒称最终全同字节。

**完整验证。** client-projection-final-v2-20261014于12:30:44–12:34:58 +08:00完成，launcher49012→runner57836。四Rebuild0警告0错误，六原dotnet命令及外层actual wait0/commands_fully_receipted=true；16TRX双配置各5434PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3857/Acceptance521/Capture243/Sessions244。397源码前后相同，65715字节/SHA `3074e9a5a6718913d592ae32d9e3b6bc04ed9ca0722b14d1d9721f56f9fdee2e`；summary10533字节/SHA `a8a1b49d820ba59e51d7e358e85e687841757081846a4a5efdd9ecd8040c6736`。

**独立核验。** verify_client_projection_20261014.py实际exit0，15977字节/SHA `7b042e4d30cd913ec2fb477eaf1faf6370ab00ecada0b5d475bf6597d2a497ed`；报告client-projection-independent-20261014.json为308067字节/SHA `bb879d7ea7b8972b50f372b3d5c2a0b9406911833b43af2b7fbe66f898c624bf`。旧5410四字段完整Counter全部保留，+24/-0，双配置相同；定向身份精确为full filter子集，正式变异原断言/字节/wait独立绑定。严格解析55TRX/28711唯一execution，另六个历史失败阶段仅核原文件/收据保持、不计正式kill；357观察原件重哈希与两次397源枚举相等。首轮full仅Debug三条命令完整wait，commands_fully_receipted=false是缺未执行Release三条，非已执行子任务未wait。核验是只读原件/前后快照，不是持续锁、OS签名、全机或全部历史审计；新核验器未执行穷举篡改自测。

**边界与下一片。** 本片没有必须先由双机人工完成的开发依赖，但M6后续实机验收未取消。下一片切屏合同及最小生命周期，再每会话画质；无新M6产品安装包，不宣告整体收口。不重验M4/M5、不开始M7、不改网络/防火墙/防护/桌面权限、不执行旧Undo、不覆盖旧ZIP。

**交付收据。** 新`outputs/LanRemote-M6-Client只读目录显示-验证证据-20261014.zip`134816211字节/SHA `62c96db6b9bddab12e20e211e1edea7083a670329dc46ae985dbb3b04c0497d3`；790载荷+1清单=791条目。含397源码、357观察原件、本片全部成功/失败阶段、执行/独立核验脚本及打包时四文档。清单156236字节/SHA `c4920f7ceabf07d36daf0ab389ff44c088a5be24ade81c65c11ee39790adccca`。生成器与主线程分别完整回读精确条目集合、逐项CRC/SHA/原输入保持，六项收据绑定通过；六份旧M6证据包及M5完整手动画质ZIP对照历史收据保持，未覆盖或重打。另交验收结果TXT、完整24身份差集TXT和证据包SHA256收据。提交后397源/357原件保持，MEMORY9983字符。本段及当日日志交付收据为打包后文档追加，不重打冻结包；代码/文档push不等于上传outputs，ZIP不是产品安装包。

### 19.50 M6 internal固定目标生产者准备（2026-10-14）

**起点与提交。** 从干净文档2ed7f7556b9e74be9eebe99f288472d9c087f0a9/代码3a4280d0157f0a0775aaa619dae8a984ac29caff继续。仅修改FramePipelineVideoProducerFactory及其Tests，2files/+481/−10，新提交`f33277ef0a388d7d4558a10bc3074e2379e648b2`；普通push成功且实读远端main完整号相同。首次push因既有127.0.0.1:7897代理不可达失败；只读确认直连443可达后，以单次命令选项直连成功，不改持久Git配置或系统网络。outputs被忽略，代码push不包含本地证据。

**为何先准备，不先冻结wire（ADR094）。** 原规格只给request_display/display_changed名称与通用字段。现有Video租约一次消费，writer取消/故障会永久终止；Stop pipeline不能收回sender或Client已接管帧。重建encoder从frameId=1起且编码首帧可能DropOldest，不能把猜测firstFrameId当已发送/显示屏障。故本片没有新增切屏codec或单飞事务，没有变动唯一Control reader、认证、第二TLS/LRVF、App/GDI与live装配。

**准备合同。** 新internal CreateForDisplayAsync(Guid, DisplayInfo, CancellationToken)固定本次不可变期望记录，不增设工厂共享选择槽；公开CreateAsync仍默认唯一主屏。完整枚举后按唯一ID和整条record值匹配，missing/duplicate/metadata变化明确拒绝、不回退主屏；同引用重复也拒绝、不按列表下标取屏。只对选中目标执行8192维度/16,777,216像素预算，且先于encoder；非目标大屏不阻止正常目标。不加wire的0..255内部限制。记录相等只是本次公开元数据相符，非持久硬件身份/拓扑锁，也不防之后热插拔。公开超预算异常仍ArgumentOutOfRangeException，但ParamName从primary改selected、文案中性化，不承诺诊断字符串逐字兼容。

**原生命周期不替换。** 两入口共享ScheduleCreate→原Task.Run→CreateCoreAsync及catch回滚；不传调度取消，不以WaitAsync/TCS取消代理提前交还原同步调用。capture/目录/完整枚举/encoder同步前缀仍在原后台任务；末次取消检查前由局部持有依赖。准备成功仅交付尚未Start producer，创建token不再控制生产；失败完整清理已取得依赖，保原异常实例、嵌套错误树、借用不释放和原Stop/Dispose join。并发调用在第一GetDisplays停闸时第二完成，再分别生产，证明固定参数不串屏；前提仍是factory每次提供专用依赖。

**新增测试与静态复核。** 11方法展开41case，原116工厂用例及旧夹具正文保持。覆盖3种位置的非主屏目标、10个缺失/重复/元数据变化、10个超预算、4个合法边界/非目标超限、同步参数/预取消、4同步前缀原Task、3迟到枚举真错误、并发独立生产、2嵌套回滚及internal公共面。目标成功测试实际Start→fake CaptureAsync收到ID→同一CapturedFrame进入fake EncodeAsync→同一EncodedFrame由真实queue/read交付→Stop/EOF/owner释放，非只查构造参数。EncodedFrame不含displayId，目标在capture/encoder入口核对。边界大尺寸只prepare/Stop，不实际采集大帧；受控1×1依赖不是GDI/JPEG/物理多屏/双机/TLS联合切屏。独立只读审查复核7491字节正式原件，无阻断；它未运行dotnet或核TRX，不冒称第二套测试实跑。

**五变异及恢复。** metadata删除整条记录比较6红；duplicate删除重复拒绝3红；target-id把pipeline目标固定73为3红；original-task加WaitAsync取消代理4红；selection-cancel删选择后取消检查1红。均为原Assert失败，非编译/Guard/Timeout/崩溃；原任务四红为AssertForwarded原断言。每项先冻结完整397源与精确old/new，实际Rebuild/test，回收原launcher/worker及dotnet完整wait，核原日志/TRX与失败断言后再精确Edit恢复原字节并全源复核。五失败stage仍status=failed/exit2；checker通过仅证明预期断言及恢复证据，绝不把测试红灯洗成PASS。未做异常中断注入或核验器穷举篡改自测。

**完整验证。** 初始target-worker-factory-01与恢复target-restored-v1均157PASS/0FAIL/0SKIP。target-final-v1于14:41:23–14:45:17 +08:00完成，launcher22284→worker26408；Debug/Release各solution及isolated Rebuild均0警告/0错误，六原dotnet命令与外层wait0/commands_fully_receipted=true。16TRX双配置各5475PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3857/Acceptance521/Capture243/Sessions285。397源码前后相同，source-before65715字节/SHA `1dae7a80059e8d112913743875ab5e42f63591b94fd720def855e91451b30460`；summary10167字节/SHA `e2883ca652d85bc68d1fce133782b9ca92d75b2e195108ecec1593dbcc81fc02`。

**正式独立核验。** verify_evidence.py于14:49:11–14:49:14 +08:00实际exit0；14123字节/SHA `165ff8c4d931ee0e8f302806f0facf17cbac3319ccf0f27635c15c4dfc4e494c`。independent-report.json为189053字节/SHA `9b6f89b66bc3e2340c3be591bf74795d2b13946c70244f5bb2886541b044133b`。旧5434完整四字段Counter全部保留，+41/-0，双配置身份相同；定向与五变异精确为full filter子集。严格解析39TRX/22163唯一execution（含旧baseline、新full、初始/恢复及五失败变异），173观察原件重哈希与两次397源枚举保持；新旧源码仅两个指定文件变化。原PID/wait非OS签名、前后快照非持续锁、非全历史或全机审计。

**边界与下一片。** 固定目标准备成功不代表运行中切屏完成；真实GDI仍仅主屏。后续先处理同一Video流连续编号/整帧边界及producer/source协调，再接Control请求/能力协商、Client消费/显示屏障及每会话画质。当前无双机人工前置阻塞，后续M6物理多屏/混合DPI/热插拔/双机验收保留。不宣告M6整体完成、不出M6产品包、不重验M4/M5、不启M7、不改网/防护/权限/Undo、不覆盖旧ZIP。

**交付收据。** 新`outputs/LanRemote-M6-固定目标生产者准备-验证证据-20261014.zip`131894153字节/SHA `a9fa24f2778c7ee9a79d965e8873378c98b6fbbbbd0f9a99e9212e9bcdf3cd77`，578载荷+1清单=579条目；含397源码、173观察原件、初始/恢复/full/五失败变异及脚本和打包时四文档。清单109616字节/SHA `b2876834ec79dddf4600b59fe9ef553f4baf6997fa4d30f82db1fe5b2d859a5a`。生成器与主线程分别完整回读精确条目集合、逐项CRC/SHA及原输入保持，五项文本收据摘要绑定通过；七旧M6证据ZIP及M5完整手动画质ZIP共八项对照历史摘要保持。验收结果TXT4424字节/SHA `2068985de749fdee2f1682bfb4b66765757d10378192d98ee9031669bb1a0615`，完整41身份差集TXT16952字节/SHA `e96f3ff92c04b9ae37be5db47a7457c16f7bef8d8200709dec9b32f66a00e500`，另交证据包SHA256收据。首次打包前长度检查在10022字符拒绝，未执行打包器/生成ZIP；精简至9944字符后成功。此段及当日日志为打包后文档追加，不重打/覆盖冻结包；证据包不是产品安装包或异机自包含环境。

### 19.51 M6 internal可选视频发送编号与原整帧生命周期（2026-10-14）

**起点与提交。** 从干净00a7fd537e2c03f94888fc73ffbb2f5e5a5fbf05/代码f33277ef0a388d7d4558a10bc3074e2379e648b2继续。仅两生产VideoFrameSender/VideoFrameWriter及其两Tests，4files/+837/−2，新代码提交`cd2bfd0e3a1ce74a26a0327f5d1b0091356b664d`。普通push成功并实读远端main完整号一致。单次直连先遇连接重置、curl超时；只读检查确认既有127.0.0.1:7897代理恢复可达后直接使用原配置成功。未写持久Git/网络配置，未重建提交/强推/跳hooks。outputs被忽略，源码push不代替证据交付。

**可选合同（ADR095）。** 默认VideoFrameSender()保留旧全ulong、重复和倒序ID透传；显式internal VideoFrameSender(ulong firstFrameId)才启用实例独占序列，允许0，不用static。编号在原来源完整退出、帧接管、caller与原整帧预算检查后，即将调用writer时分配；每次递增、MaxValue仅分配一次，不回绕。Max之后仍可读来源EOF并正常结束；若又交付帧，则在原写前预算复核后以InvalidOperationException拒绝，finally释放额外帧。等待来源本身仍无写预算/硬中断，caller取消及来源原错误分类不变；不允许重用sender，也不重试失败半帧。编号代表写入尝试顺序，不保证已写首字节、完整发送或Client消费/显示。

**writer原任务及所有权。** 新WriteFrameWithIdAsync(EncodedFrame, ulong, CancellationToken)与原入口直接返回同一private async核心原Task。先FromFrame完整模型验证，然后仅header with FrameId，借用同一原payload有效切片；原frame.FrameId、owner不修改/不重包。两入口共用原单飞状态，header→payload→Flush→末检完整流程、失败永久终止与一次流释放/稳定清理错误。sender不预取，原write/flush退出前不释放frame；保主异常原实例/错误树及OCE token，不用取消代理。IsTerminated/Dispose被调用不证明原IO退出或真实资源释放。Router构造点仍new()，本片没有产品可选接线、GDI/Control/Client或认证/wire格式变化。

**新用例。** 7个sender方法展开23case、11个writer方法展开38case，合61；初始及恢复两类合147PASS，旧86case及其夹具正文保留。覆盖0/1/高位/Max边界、重复来源ID、两个独立sender同起点、原帧/同一payload借用、Max后EOF或额外帧、迟到来源frame/EOF/fault、header/payload/flush原IO拒合作取消与时限、重入不关当前writer且不预取、原异常树/取消实例/清理根、精确期限前一tick/等于/后一tick、非法模型先于payload及首字节、双入口交叠和永久终止。golden只以BinaryPrimitives改原手工wire[8..15]形成独立期望，不用生产writer自证。受控不透明5字节payload不宣称可解码JPEG，更非真实TLS/GDI/物理多屏/双机切屏验收。

**五变异。** header-id删覆盖3红；increment停递增4红；exhaustion绕过耗尽1红；owner-before-join提前Dispose但仍await原写出6红；post-flush-budget删最终绝对预算2红。每项冻结原397源和精确old/new，串行Rebuild/test，核原launcher/worker/dotnet完整wait、TRX错误及日志后再精确Edit恢复，全源摘要一致。全部为原Assert失败，不是编译/Timeout/Guard/崩溃；失败stage仍failed/exit2，核验通过不将原失败改成PASS。owner变异仅证明提前释放敏感性，不冒称取消代理或所有join删除的单点kill。未做全异常中断或核验器穷举篡改自测。

**完整验证与独立核验。** sequence-focused-v1/sequence-restored-v1各147PASS。sequence-final-v1于16:57:11–17:01:35 +08:00，launcher29576→worker45588；六原dotnet PID为35436/39164/45212/48536/47556/42372，均wait0。Debug/Release各solution及isolated Rebuild零警告/零错误；各5536PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3918/Acceptance521/Capture243/Sessions285。summary10208字节/SHA `0178e850d9b3ab482d9481ee78d7cb3bfffb0f46c07a43764e0cd7b9e1955cc2`。正式独立核验实际exit0，report186922字节/SHA `c361589032cf038c6fb35a8a1a8f05e7c3f665f5176baf5381ca0a897deb11d1`；旧5475完整四字段Counter保留、+61/-0，两配置身份相同，定向/五变异filter精确子集，397源仅四指定文件变化，174观察原件重哈希及两次源枚举保持。39TRX/22334唯一execution包含旧baseline、新full、初始/恢复和五失败变异，不是39次full；PID/wait不是OS签名、前后快照不是持续锁。

**下一片与范围。** 下一片为同一Video流source/producer换代生命周期：协调旧producer Stop/Dispose/原Read与sender已持帧，保唯一sender/writer及原Video租约，不靠重连、EOF或写取消冒充暂停。之后再冻结Control切屏请求/能力协商、Client消费和显示屏障、任意目标GDI及每会话画质。当前无双机人工前置阻塞，M6后续实机验收保留；不宣告M6整体完成、不出M6产品包、不重验M4/M5、不启M7、不改网/防护/权限/Undo、不覆盖旧ZIP。

**交付收据。** 新`outputs/LanRemote-M6-可选视频发送编号-验证证据-20261014.zip`131965892字节/SHA `6a3c772c29470273f01f016784e63e37447fd3e58e1f1103aa4be789a0fd60c5`，579载荷+1清单=580条目。清单109758字节/SHA `eb08f0aef68cbe86175edf97ea2d235078b28aa90133293522c79376ef7d068b`；生成器及主线程独立完整回读精确集合/逐项SHA256/CRC、包外原输入及收据绑定通过，九旧ZIP摘要保持。验收TXT4364字节/SHA `878b7fbb691ed34c2b5c5e9cfca07e3f6429302d97c92575af29be2ab0fbf193`；完整61身份差集22208字节/SHA `63d539aca87f6723d9176de24fde6c53bd20b2518dd1c8bb0d54ba0854185d42`，另交SHA256收据。MEMORY先在10011门禁拒绝，最终精简9949后再打包；没有覆盖旧文件。本段和当日日志为打包后追加，包内四文档保持打包快照、不重打；证据不是产品安装包或异机自包含环境。

### 19.52 M6 internal同流生产者换代来源（2026-10-14，已推代码6363e53）

**起点与源码。** 承接干净45d6798文档/cd2bfd0代码，不重复编号实现或M4/M5验收。新增`src/LanRemote.Transport/ReplaceableVideoFrameSource.cs`及`tests/LanRemote.Transport.Tests/ReplaceableVideoFrameSourceTests.cs`，既有397源码项均未改；代码提交`6363e537a477ba1bcaec03f2c720293ee795474f`，2files/+1786/−0，普通push并实读远端refs/heads/main一致。未改持久Git/系统网络配置、未跳hooks或重写历史。ADR096记合同；不修改原实现规格。

**内部合同。** 单session/单未完成Read/单Replace，无替换排队。初始空来源等安装而非EOF。非async `ReplaceAsync(next)`同步验证；正常返回Task就接管专用且未Start候选，异步失败也不交还；拒绝新对象则未接管，重复已拥有对象不改变归属。gate封口旧代并发布真实worker后，在gate外分别调Stop/Dispose/Completion；原Start、原底层Read同步前缀/异步尾部、迟到frame Dispose全部结算后才Start并发布候选。不等待整个外层source Read，否则其等待换代会自join。一个清理同步入口阻塞不能阻碍另两个入口发出；原调用不被取消代理替换。

**所有权与退出。** 帧交付在gate内裁决：提交先赢则归读者并清空局部frame；退役/Dispose先赢则锁外释放迟到帧一次。已交sender帧不收回，Replace不拥有或取消writer。活动代EOF永久终止，退役EOF仅内部过渡；不把EOF当暂停，不复活或重连Video租约。当前无active Completion自动监视器，由EOF/真实读取故障/Dispose终止。稳定Completion先join代际原根及Replace，发布内部settled让外层Read退出，再join该Read；依赖与owner回调不得等待来源自身操作，未知同步调用不能被硬中断。

**错误。** 读取原Task多根、嵌套Aggregate及原异常引用保持。Stop/Dispose/Completion同原Task去重，不同原操作同一异常实例仍保留多个位置。读取真实故障及迟到owner清理错误同时进入Replace失败与Completion；清理OCE/原Task Faulted OCE不当普通取消。caller token真实Canceled读只取消本次Read。Start只凭退役已观察+原异常显式证明豁免停止抢先拒绝；证明探针自身错误另保留，不事后翻判。任务退出只证调用结算，不保证发生清理异常后的资源实际释放。

**新增测试与实际组合。** 36方法展开48case：协作者只写45case未执行，主线程读完后补3case并统一运行。初始/恢复定向各195PASS，含新来源48及旧sender/writer147。涵盖冷空安装、连续替换、session/单飞/候选归属、迟到frame/EOF/fault及owner阻塞、原Read同步前缀/异步尾部、阻塞Start并发Dispose、独立清理入口/任务、原错误树/共享Task去重/清理OCE、EOF/稳定Completion、同sender跨代连续编号。主线程3case分别把旧帧header/payload/flush原IO卡住，完整await Replace成功后仍断言旧owner未释放、writer token未取消、新producer未被预取；放行后实际wire为同sender70/71两帧。另一组合从旧代Read等待换代后实际wire41/42。golden由手工LRVF数据独立改编号，不使用生产writer生成期望；不透明5字节payload不宣称可解码JPEG。

**五项变异。** late-owner漏释放旧代迟到帧1红；delivered-owner不清局部归属导致已交帧误释放2红；shared-task不按同原Task去重1红；original-roots只取第一根1红；replace-read-error漏汇合旧读/owner真错2红。各有原399源、精确old/new意图、原进程日志/TRX/wait、checked和精确恢复收据。均为Assert断言失败、不是编译/Guard超时/崩溃；原failed/exit2保持。**这五项不构成原Task join删除或取消代理替换的独立kill证明**；部分等待断言为真实gate进入后的状态检查，不冒称原awaiter身份反射核验或穷举竞态。

**完整回归与独立证据。** `outputs/m6-video-generation-20261014/generation-final-v1`于19:28:34–19:32:43+08:00，launcher45176→worker28552；六dotnet PID37296/26652/46444/37632/15524/50968均完整wait0。Debug和Release各solution/isolated Rebuild零警告/零错误，各5584PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport3966/Acceptance521/Capture243/Sessions285。summary10248字节/SHA `c378ce601e8690f7f799859ba77f04e630ecdba402a71a99ae28df76c660be7b`。正式独立核验实际exit0：旧5536完整四字段Counter+48/-0，399源仅新增上述两文件、175观察原件重哈希保持，两配置身份相等；39原TRX/22638唯一execution（旧full+新full+初始/恢复+五失败变异，不是39次full）。报告176429字节/SHA `6a83b0e65e2a1780d771dc501d338b02ba5322a4468c052a65236116761688de`。另一次当前代码只读审查未找到明确阻断，未将其当实跑证据；PID/wait非OS签名、前后快照非持续锁。

**下一片。** 继续经认证会话内部换代接缝与生命周期装配，再冻结Control请求/响应及Client帧消费/显示屏障。当前Router仍`new VideoFrameSender()`与FixedProducerFrameSource，未接本来源，未改Control codec/UI/GDI/FramePipeline/认证/ViewOnly/父资格/单次Video租约。后续仍需live切屏、每会话画质、真实多屏与双机验收；本片受控producer/Stream不是实际GDI/TLS联合或真人实屏，M6未整体完成。无M6产品包，不重验M4/M5、不启M7、不改网/防护/权限/Undo、不覆盖旧ZIP。

**交付收据（打包后追加，不重打）。** 新`outputs/LanRemote-M6-同流生产者换代来源-验证证据-20261014.zip`132093125字节/SHA `ce76d535d17a79c8faed42eabe0ca7251b41fea2340129f5799e516f55ca1aeb`，582载荷+1清单=583条目。清单111204字节/SHA `ef877f67c16e78125aa7e30a7bfe487394fc4ee160d0e915d5067d9d2513edd6`；生成器及主线程独立完整回读精确条目集合/逐项SHA256/CRC/包外原输入通过，十个旧ZIP保持。验收TXT3845字节/SHA `18c43c8a7323c8630dade69e949d354640faf35ee8b761fb4e070adbb69488a5`；完整48身份差集17511字节/SHA `631a501345cd107da0b959108825cb1732a98c06c6fd57c248520da1e98d2a98`，另交SHA收据。MEMORY经精简最终9973字符。本条与当日日志是打包后记账，包内文档保留打包快照；证据不是产品安装包或异机自包含环境。

### 19.53 M6 父资格约束的视频来源发布接缝（2026-10-15，代码提交见下）

**起点与范围。** 承接干净99002ad文档/6363e53代码。本片收窄为父资格约束的内部换代入口原语，完整Router装配留后片：新增`tests/LanRemote.Transport.Tests/VideoSourcePublicationTests.cs`（20方法/31case），修改`SessionRegistry.cs`（新增internal `PublishVideoSource`与`SessionRegistration.ReplaceVideoProducerAsync`）、`VideoAttachLease.cs`（真实租约构造可选共享registry gate，`Complete`同gate）、`ReplaceableVideoFrameSource.cs`（仅新增只读internal `SessionId`）、`VideoAttachRegistryTests.cs`（内部方法白名单有意加入PublishVideoSource）。400源码项=399+1新增测试文件。public API、Snapshot、秘密导出面、Register唯一认证调用点均不变。

**合同要点（ADR-097）。** 发布在registry同gate核真实entry的`VideoLease`引用（防同Guid伪造/跨registry/ABA）、source只读SessionId、控制取消、lease Revoked/Completed；同一租约只发布一次，摘槽不恢复资格，publication Dispose只清所捕获entry的槽。`ReplaceVideoProducerAsync`非async，同gate核当前entry引用/句柄释放/取消/子终态/已发布槽后**直接返回`source.ReplaceAsync(next)`原Task**：同步拒绝不接管，正常返回即按ADR096移交，无代理/排队/超时取消。锁序固定registry→source；source同步入口只登记worker，测试以独立线程探针证实外部producer Start/Read/Stop阻塞不持registry gate。撤销三态（publication Dispose/Unregister/lease Complete）只拒绝后续接纳，不撤回已接管候选——先接纳的候选可能在注销返回后Start，由生命周期owner负责Dispose source并完整join。控制CTS不与registry gate同锁，只承诺检查点取消观察；AttachWindow仅限初次附着，窗口到期不拒在流换代。发布前置（ACK完整退出、初装成功、sender原worker登记）是调用方合同，本原语不验证。

**测试与变异。** 初始/恢复定向各150PASS（新31+旧登记表/换代来源119），源码前后一致。五项生产字节变异（全在SessionRegistry.cs）：lease-identity删租约引用核对**2红**；withdraw漏摘槽**1红**；original-task以`Task.WhenAll`包装原Task**未击杀**（单任务WhenAll本运行时返回原Task，Same断言仍绿，如实记not-killed并保留绿运行收据）；original-task-proxy换`ContinueWith`确实不同代理**1红**补足原Task身份敏感性；parent-cancel删父取消核对**1红**。每项完整wait/核验/精确恢复，非编译/超时/崩溃。阻塞Start时摘槽/注销可完成、显式停止仍须join Start与Stop/Dispose/Completion三原尾任务；同步拒绝候选在source完整收尾后仍断言未被触碰并由调用方释放。

**完整回归与独立核验。** `outputs/m6-video-publication-20261014/publication-final-v1`于13:53:29–13:57:49+08:00，launcher→worker→六dotnet均完整wait0，四Rebuild零警告/零错误；Debug/Release各5615PASS/0FAIL/0SKIP（Core197/Security66/Protocol303/Integration3/Transport3997/Acceptance521/Capture243/Sessions285），summary10264字节/SHA `38f6c5e02bc9328454d76d25c8ba12cf8fc62c6eb3706c4a27834a8f7a37b123`。正式独立核验实际exit0：旧5584完整四字段身份保留、+31/-0全部归属VideoSourcePublicationTests、两配置身份相等、400源码项、39TRX/22707唯一execution、观察原件重哈希保持。恢复定向150于13:53:18–13:53:29。

**边界与下一片。** Router仍`new VideoFrameSender()`+FixedProducerFrameSource未接；发布方前置（ACK后发布、退出先摘槽再独立通知close/cancel/source Dispose后join原sender/peer/source Completion/取消/close、租约Complete仍在Router最外层）与source早期终态通知、唯一权威错误槽留装配片。不宣称发送/显示屏障或live切屏；不改Control codec/UI/GDI/FramePipeline/认证/ViewOnly/单次租约。M6未整体完成，无M6产品包；不重验M4/M5、不启M7、不改网/防护/权限/Undo、不覆盖旧ZIP。

**提交与推送。** 代码`62afdf0f3725dae25116b6580ccb04206aa60e6b`（`feat(m6): add parent-constrained video source publication seam`，5files/+884/−4，含新测试815行）。首次与第二次push因本机直连github超时/连接重置失败（HTTP探针github.com与api.github.com也超时，curl exit35，非凭据问题），第三次普通push成功；实读origin refs/heads/main完整号一致。未强推、未改历史、未跳hooks、未改持久Git/系统网络配置。ADR097见`docs/DECISIONS.md`。

**交付收据（打包后追加，不重打）。** 新`outputs/LanRemote-M6-父会话视频换代发布接缝-验证证据-20261015.zip`132125321字节/SHA `c24e06c16f6025943feab0eacada40e37f307963948dcccf27b7b277f5e2bcf2`，582载荷+1清单=583条目。清单111924字节/SHA `348989ec084716bde52fd2feb8eea96c9a0422fa0f42a7f9fdad03e8a9fafda3`；生成器及主线程独立完整回读精确条目集合/逐项SHA256/CRC/包外原输入通过，十一个旧ZIP保持。验收TXT5811字节/SHA `2671b28d89d7706b471de48ac8148f7cb6d7d09e9cbea80eb3028d2f1cd71087`；完整31身份差集11181字节/SHA `d8b07065dfcebdd5364dea104e892a0bee6279e5d32ff43c9a28d7e24d491e70`，另交SHA收据。另新增仓库根`新对话接手prompt-20261015.md`（用户要求：每次新开对话复制粘贴即可按“先读哪些文件”接手，含不做事清单）。MEMORY最终10005字符满足上限。证据不是产品安装包或异机自包含环境。

### 19.54 M6 Router内部可选换代生命周期装配（2026-10-15）

**接手对齐。** 本片开始按用户指定顺序读取索引，工作树干净，本地/远端最新文档提交3e0e757一致，最近代码62afdf0已包含。发现第1节和顶部仍滞后§19.53，先按既有收据对齐，未重跑历史通过项。此前10005字符满足10000上限的文字为旧记账错误，本片已将MEMORY实际长度收敛到10000以下；不重打旧包。

**实现范围（ADR-098）。** 8files/+967/−13：修改FirstFrameRouter、AcceptedConnection、HostOwnedVideoStream、ReplaceableVideoFrameSource、VideoFrameSender五个生产文件，以及两既有Router夹具；新增FirstFrameRouterGenerationTests.cs，14方法/36case归属既有partial FirstFrameRouterBoundaryTests。Router工厂构造显式可选enableProducerReplacement/firstFrameId，默认旧路径不变，矛盾参数同步拒绝。ACK原写/Flush及末检查结束才factory→初装；初装成功、sender原worker登记后以真lease发布。source同流换代/可选sender编号仅内部可用，未改App默认装配。

**生命周期。** 入口共享VideoPublicationLifetime gate串行发布/停止/摘槽；host取消先摘槽再通知原固定close监督，覆盖附着后的同步clock前缀。writer先行关闭通过HostOwnedVideoStream可选beforeClose先摘槽并触发稳定StopRequired，两处WhenAny都消费，避免sender帧释放与物理close双阻塞时停止通知缺失。peer在外部同步前缀之前登记worker。source.Termination仅暴露已有closingSignal；退出先摘槽，独立通知close/cancel/source.Dispose，再完整join setup内原factory/初装、sender、peer、source.Completion、取消和close，最外层最后Complete租约。迟到未接管factory候选由局部owner独立Stop/Dispose/Completion并join，仅原Task相同才去重。

**错误合同。** 新路径source.Completion为唯一来源错误槽，初装/Replace/sender传播只join不累计；sender.SendFailure与来源/帧释放/writer释放分槽，不用异常引用去重不同操作，不Flatten原错误树。新路径来源Aggregate形状明确保留，旧Router默认编号及sole-error顶层保持。

**验证及保留失败。** initial-v1为测试编译19error/0warning，测试未运行，原件及已启动命令wait保留；v2定向339绿后仅补新测试时序，作为历史绿，不当最终同树。initial-v3与restored-v1同树各339PASS/0FAIL/0SKIP。三项生产字节变异writer-withdraw/source-double-count/sender-sequence各1原断言红，非编译/超时/崩溃，逐次完整wait、check0并精确恢复。未把早期通知或未接管Completion的超时/调度窗口计为独立kill，不穷举全部join删除。

**完整回归。** `outputs/m6-router-generation-20261015/router-generation-final-v1/summary.json`于15:38:21–15:42:38+08:00 passed/exit0；launcher→worker→六dotnet均完整wait0，四Rebuild零警告/零错误。16TRX Debug/Release各5651PASS/0FAIL/0SKIP（Core197/Security66/Protocol303/Integration3/Transport4033/Acceptance521/Capture243/Sessions285）。summary10378字节/SHA `be6e72a44781ee3933c69bb8eac375ed281d9af1908ee3cd7a6034441671bdb4`。

**独立核验。** verify_evidence.py原进程实际exit0、主线程完整wait0，报告170988字节/SHA `5148fcece65d505906cccca7a492f22cc31f0a3fec851b3034a4e9f85a595337`。旧5615完整四字段Counter保留、+36/-0，双配置身份相等，38原TRX/23553唯一execution、165观察原件重哈希保持。402源码快照=旧400+新测试+旧冻结后历史根接手prompt；后者5108字节/SHA `830897614440b586f3a75b7a35d4590b9beacc1839163f8cbba71488968c55bb`独立钉住并从implementation_delta剥离，本片实际实现仍8files。前后快照非持续锁，PID/wait非OS签名。

**审计输出绑定修正。** 首次打包前重哈希发现v1报告包含当时仍在写入的自身stdout日志（观测时0字节、退出后94字节），打包失败且未创建ZIP；不将首份exit0冒称可交付绑定。保留原报告/日志/退出收据及`verify_evidence_v1_preserved.py`。隔离核验自身输出后另写`independent-report-v2.json`，原进程及外层实际exit0，报告172650字节/SHA `60bf4ca371c933914e709a79e379a243dc8c8746281638b6a9d1153dbd7466ef`；169观察原件在退出后重新核对无变化。源码/构建测试未重跑、旧证据不覆盖；正式交付以v2报告为准。

**提交推送。** 代码`029032b2607eacc058f69d06a21e972c8ab8083f`，`feat(m6): assemble optional router video generation lifetime`。普通push成功，实读远端refs/heads/main完整号一致。无强推/amend/历史改写/hooks跳过/持久Git或网络配置修改。后续文档提交可晚于此代码号。

**证据交付收据。** 独占新包`outputs/LanRemote-M6-Router换代生命周期-验证证据-20261015.zip`，132325413字节/SHA `c0611c783edf3b88e4cfc1763d6da40ed895ee181665fb1ac0a15fe5a1ae5eb0`，580载荷+1清单=581条目。生成器15:57:10及主线程15:57:42独立完整回读逐条SHA256/CRC、包外输入和十二个旧ZIP均通过；原收据为本片目录`package-verification.json`和`main-independent-package-readback.json`。清单112838字节/SHA `655857e2e634fe5d04bcb61ee0979504369db5419e619f0146f877b2587e41a4`。另交验收结果TXT5183字节/SHA `4eaa65eb8975cdf5723e2d4bcd0207afa31d780f563380bb0fe471c2d160c16b`、完整身份差集TXT14208字节/SHA `246c696626fe14969ab3af4a7dc8d9256cab130dd8f5159ad900010a78c4dd14`及证据包SHA256收据。续接于16:05再次计算新ZIP、十二旧ZIP、验收TXT和身份差集共15文件摘要，均与打包收据一致；未重跑构建/测试。包内文档为打包时快照，本段及后续记账不重打、不覆盖该包。此为验证证据，不是M6产品安装包；源码push不替代文件交付。

**边界及下一片。** 此为受控Task/Stream验证的内部显式装配，不是TLS/GDI/双机/真人/物理多屏联合验收；不把可观察通知夸大成已严格定序到第二WhenAny。默认App尚未启用新装配；下一片按切屏合同、Client显示屏障及每会话画质的既定顺序，先完成经认证Control切屏消息与单会话协调的最小合同，再接产品路径。不可将换代完成/编码首帧/发送尝试编号当发送或显示成功边界，不重连单次Video租约。M6未整体完成，无M6产品包；不重验M4/M5、不启M7、不改网络/防护/权限/Undo/旧ZIP。

### 19.55 M6 Control切屏请求纯合同与父会话单槽准入（2026-10-15）

**起点与范围（ADR-099）。** 起点工作树干净，本地与实读远端644c8d1一致，代码029032b已包含。原规格仅列request_display/display_changed和通用seq/timestampMs；本片只冻结请求及无资源准入，不把尚无发送/显示边界的换代完成伪装成成功回执。4代码/测试文件、+1562/-0：新增DisplaySwitchRequestFrame及两测试类，SessionRegistry仅追加新内部入口/entry槽，既有Replace/Router/App不改。

**请求纯载荷。** 七字段type=request_display/protocol1/sessionId/seq/timestampMs/catalogSequence/displayId，1..4096字节；非空canonical会话关联、非负Int64元数据、0..255原始显示索引。字段完整且唯一，转义等价重复也拒；严格拒类型错/浮点指数/溢出/未知字段/尾JSON/非法UTF8/孤立代理。只纯解析/序列化，不触流或时钟；profile2不因本片自动承诺live切屏能力。

**准入与边界。** 真registration同registry gate创建一次、entry保存实际准入实例身份，关闭不可重新创建；仅父活动、真lease未撤销完成、来源已发布且未观察到Termination才可接纳。同gate一项无资源预约，无队列/Task/计时器；Unavailable→SessionMismatch→Busy→StaleSequence→CatalogMismatch→DisplayNotFound→Accepted。只有Accepted消耗seq，首0/跳号及Max一次后耗尽，不回绕；目录序号只匹配本Host快照元数据，非拓扑新鲜性。ViewOnly/Control都可选屏且不提升权限。预约保留原request/目标record，Dispose只退自己槽；准入Dispose仅封口，不停止source/完成lease/注销父。IsCurrent是瞬时快照，不能跨锁用作授权；最终固定目标准备须重新枚举唯一ID/完整record，真实Replace仍过原父gate。

**实际定向及历史差异。** v1/v2各490PASS/0FAIL/0SKIP且运行内405源保持；之后分别补权限不变断言和独立转义重复Fact，末核发现差异，未沿用旧绿。停止协作者写源码后initial-v3正式同树491PASS（codec312/准入36/旧Router112/publication31），当前源与前后清单再次一致。独立核验用固定SHA及精确逆删除字节桥确认两历史变化，不只放宽白名单；新旧身份差异精确为一个新增Fact。恢复定向491通过。

**变异。** 四项生产字节变异context-binding/admission-identity/sequence/catalog-binding分别1/2/1/1原断言失败；每项原launcher/worker/build/test完整wait，check实际exit0，再精确恢复原字节与完整405源。不是编译失败/超时/崩溃，不将变异红记为未修产品失败；没有宣称穷举全部门禁或竞态。

**完整验证。** `outputs/m6-switch-contract-20261015/switch-contract-final-v1/summary.json`17:43:32–17:47:48+08:00 passed/exit0，四Rebuild零警告/零错误，六dotnet及launcher/worker原进程完整wait0；16TRX Debug/Release各5999PASS/0FAIL/0SKIP。两配置分项Core197/Security66/Protocol303/Integration3/Transport4381/Acceptance521/Capture243/Sessions285。summary10335字节/SHA `2e52eec1f6f903d09aa03f24dfafcd1948d7296dd17531396c5769fed18fa8c5`。

**独立审计。** 17:48:14–17:48:18原进程实际exit0，主线程完整wait0；报告`independent-report-v1.json`316954字节/SHA `b09ce7032422ac9125664d0f1fbf3f0e0e18970c3d4149838a84212009b188d3`。旧5651完整四字段Counter全部保留、+348/-0，两配置身份相同；405源、40原TRX/25267唯一execution、183观察原件在退出后再次核对。核验stdout和退出收据先写本片目录外、进程结束后复制稳定原件，避免上一片自身输出绑定问题。前后快照非持续锁、PID/wait非OS签名，不运行旧构建测试。

**提交。** 代码`580fc125e326ade56c82a95a81ac708b12978ade`，feat(m6): add strict display request and session-bound admission；已普通push并实读refs/heads/main一致。无amend/强推/改历史/跳hooks/持久Git或系统网络配置更改。后续仅文档记账，不重写验证源码。

**证据交付收据。** 新包`outputs/LanRemote-M6-Control切屏最小合同-验证证据-20261015.zip`132787047字节/SHA `33dded8285752249993f0cce79ce63b21db9ee917aa15d092505629f703b055c`，596载荷+1清单=597条目。18:04:16–18:04:26打包原进程完整wait0，生成器逐条SHA256/CRC回读、包外输入与十三旧ZIP保持；18:05:40主线程独立完整回读再次通过，405源与183审计观察原件仍一致。原收据为本片目录`package-verification.json`、`main-independent-package-readback.json`和目录外`outputs/m6-switch-contract-package-v1.execution.json`。清单116079字节/SHA `2dda39a542c922a607676ae35c37dc9803f5e4360b97a655fc81939bf82fd7e3`；验收TXT5314字节/SHA `cc30e260ac664ecafdc6d064d47d728ccf43ce7798a9f75c1f26d64e63b6e822`；348完整身份差集TXT135855字节/SHA `5203c8aca37c5423d401e9c790cbf7e584e41a496a6f12ecf4f406b6f60b99f9`；另交证据包SHA256收据。包内文档是打包时快照，本段及后续文档记账不覆盖重打原包。本次收尾未重跑构建/测试；这是验证证据，不是M6产品安装包，源码push不替代四份文件实际展示。

**下一片。** 单会话拥有型请求执行生命周期：从预约目标进入固定目标准备，明确原factory迟到候选归属/同步拒绝回收/正常返回Replace原Task移交，停止独立通知和完整join；然后才能接真实发送成功边界、Control成功回执、Client显示屏障、产品路径和每会话画质。本片没有这些执行逻辑或live读写接线，无display_changed/成功ACK；不把accepted/IsCurrent/Replace完成当发送或显示成功。受控producer/Task不是实际TLS/GDI/双机/真人/物理多屏联合验收。M6仍进行中，无M6产品包；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP。

### 19.56 M6 内部切屏请求执行器的拥有型生命周期（2026-10-15）

**起点与范围（ADR-100）。** 起点本地/远端44a5e0947e3b37196898002f8ae93826cb938e49一致，§19.55四份交付已展示。新增DisplaySwitchExecutor与测试，SessionRegistry仅追加真实准入装配/执行身份/最终预约绑定接缝；3代码/测试文件，+1365/-2。未改原ReplaceableVideoFrameSource/Router/App或Core公开接口，不接Transport→Sessions产品依赖。

**准入与单飞。** 真实Admission同registry gate一次CreateExecutor，已有手动预约拒绝且不接管；装配后普通TryReserve不能旁路。Unavailable→SessionMismatch→Busy等原优先级保持；原执行Task未完成仍Busy，不以预约finally释放判断可重入。测试反射主动释放预约隔离原Task守卫，不冒称自然调度窗口证明，不增加生产hook。

**prepare与移交。** 共享gate先发布worker，锁外调用prepare同步前缀并持有原Task。成功返回先取得局部候选owner，再检查停止；取消后的迟到候选不会丢失。候选必须专用/未Start/未由其他来源拥有，失败前未交出依赖由factory负责。最终预约/执行器身份/关闭状态/父资格与原Replace调用在同registry gate，正常同步返回原Task才清空局部owner；同步拒绝保留本地归属，异步失败不返还候选。结果Transferred/ReplacementCompleted仅历史事实，非发送/显示成功或当前权限。

**完整收尾。** 未移交候选StopAsync/DisposeAsync/Completion getter三路独立worker，任一同步阻塞/抛错不跳其余根；完整join、仅相同原Task引用去重、不Flatten错误树。RequestStop同gate封口/发布StopRequired并登记独立取消worker，取消回调不会内联堵塞通知；Completion等原执行与取消worker退出才Dispose CTS。只抑制本令牌已取消且真Canceled原Task/同步OCE，faulted OCE不吞。执行器不拥有source/lease/流、不自动监听父退出；外层须显式通知且先独立停止source等依赖再join，禁止工厂/生产者/回调反向等待本执行器。

**定向与保留历史。** 新66case。initial-v1原build为3warning/2error（同步Task返回接口断言误选xUnit异步重载及3处未显式丢弃Track返回）；测试未运行，build退出1/worker及launcher退出2原件保留。修正后initial-v2定向180绿；之后补原Task单飞Fact并将Fixture最终Drain改为直接等待原根，不再用watchdog到期遗弃。initial-v3正式同树181PASS，恢复定向181PASS。两段精确逆替换+三版固定测试SHA核实历史差异，只有v3当最终初始证据。

**五项生产变异。** reservation-current、owner-transfer、cleanup-task-identity、original-error-tree、active-task各1原断言失败，build均通过、原test退出1/worker及launcher退出2、检查exit0后精确恢复全部407源。预约门禁仅admission-close分支红（其余父门禁仍有效），单飞门禁由主动释放预约隔离；不是超时/崩溃/编译失败，不宣称所有门禁单点kill或穷举竞态。

**完整回归与审计。** final-v1于19:39:56–19:44:06+08:00，四Rebuild零警告错误、16TRX双配置各6065PASS/0FAIL/0SKIP；分项Core197/Security66/Protocol303/Integration3/Transport4447/Acceptance521/Capture243/Sessions285。summary10342字节/SHA `71c1b78a2a7aa2cd34de5a274959b76230d84bd8a762e1f8afecd17696321c43`。原launcher记录worker/六dotnet完整wait0；主shell后台句柄已丢失，未伪造该句柄回收成功。19:52:59–19:53:02隔离输出正式审计原进程45604退出0，主线程完整wait0；报告202277字节/SHA `2fe111323054f3c6c849517d5210cb7ff251e1fea4b573e4217b17201fddeefa`，旧5999完整四字段Counter+66/-0，407源、40原TRX/24680唯一execution、195观察原件退出后再次核对保持。快照非连续锁、PID/wait非OS签名，旧基线只读核对未重跑。

**提交。** 代码`35a372dbf0d2a1efbd089638ec5fe305ea1eca54`，feat(m6): execute display requests with owned candidate lifetime；普通push成功并实读refs/heads/main完整号一致。不强推/amend/改历史/跳hooks/持久Git或系统网络配置更改。

**证据交付收据。** 新ZIP `outputs/LanRemote-M6-切屏请求执行生命周期-验证证据-20261015.zip`132807180字节/SHA `ea34fb0edbe839ac074615554e4f2b22d9f81a49a73b9c3339532166ec091f64`，611载荷+1清单=612条目。20:04:43–20:04:53原打包进程9600由包装器完整wait0，生成器逐条SHA256/CRC、包外输入及十四旧ZIP核对通过；主shell后台句柄丢失不补造回收。20:11:44主线程独立完整回读再次通过，407源/195审计原件保持。首次打包因MEMORY10015字符超过10000门禁退出1，尚未创建ZIP；压缩记忆至9969后使用新包装标签v2，不改脚本/旧收据、不重跑构建测试。清单119969字节/SHA `78afbb01ef0bb633e3c6a0e6828da97f98eb9629c4eb1c6dd3f9eb5b18d62170`；验收TXT4839字节/SHA `6f3512db2b390d385d45057863d9f6f43be386fe1966d901c4bc20d0e3044a33`；66完整身份差集TXT23036字节/SHA `ebf4afdacf4e9c49ff29b3107a9c9be7d6e74f3bba59d62ef5269a9077c1157c`，另配SHA收据。原件见本片package-verification.json/main-independent-package-readback.json及目录外m6-switch-executor-package-v1/v2退出收据。包内文档为打包时快照，本段后置记账不覆盖重打原包。

**边界与下一片。** 未接Sessions固定目标工厂/GDI、Control唯一reader/writer或App默认路径；无成功回执、真实发送首帧边界、Client显示屏障或画质。内部受控producer/Task及真实registry/lease不是双机、真人、TLS联合或物理多屏验收。下一片先真实整帧发送成功边界与换代关联，再Control回执/显示屏障/产品路径/画质；不把Replace完成当发送或显示成功。M6未整体完成，无M6产品包；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP。

### 19.57 M6 显式本地整帧发送成功与帧代际关联（2026-10-16）

**起点与范围（ADR-101）。** 起点本地/远端899b82e9d0988aab2ac0b2493e3f7e986025d8e1一致，§19.56已交付。仅修改ReplaceableVideoFrameSource/VideoFrameSender并新增VideoSendBoundaryTests，3文件/+802/-8。不改Core EncodedFrame、IVideoFrameSource、writer、Router/App或LRVF格式。新增构造(firstFrameId, source)才启用追踪；两个原构造不变。

**归属与成功。** ReplaceAsync(next,out identity)保持原换代Task和同步接管判据；每次ReadForSendAsync独占ReadOrigin，与普通Read共用单读者及原worker，在帧交付gate内才填写真实代际，不按写完时当前活动代反推。身份不含producer/source/frame引用、不是权限。原writer的头/payload/Flush/终态检查完整成功后，还须通过sender绝对预算及caller取消末检，才Volatile发布不可变快照：同一成功代首帧及最新帧的实际wire编号/宽高。部分写、取消、关闭、精确到期不发布新成功；后续owner清理失败不撤回既成历史。

**换代与单槽局限。** 已提交旧帧不被Replace收回，其成功仍归旧代；只有新代真正成功写出才更新身份，零帧中间代不伪造成功。仅单槽元数据、有界、无订阅回调或额外worker，不保证消费者观察每个代际。默认Router/App仍不装配新构造，后续必须另建具体请求/代际的可靠结算，不能轮询单槽冒充成功回执。历史不是当前资格、远端接收/解码/显示证明。

**确定性测试。** 新13方法/38展开case；header/payload/Flush三个原IO尾部各有success/fault/cancel/deadline/closed，尾部忽略取消以证必须原Task退出才释放owner。手动单调时钟推进预算-1/0/+1tick且不触发timer，隔离sender末检查。另覆盖首/末不可变、失败保历史、跨三次换代旧帧在途、旧读迟到丢弃、绑定拒绝、默认不追踪、MaxValue/额外帧、owner清理错误和普通/追踪读共用门禁。正文watchdog只诊断，finally放行所有gate后直接join原根。首次与恢复定向均233PASS，未产生编译/普通测试失败历史；不宣称穷举所有停止/清理组合。

**五项生产变异。** early-publication/final-budget/stale-generation/first-overwrite/source-frame-id分别24/2/3/4/15原断言失败。每次原build通过、test退出1、worker及launcher退出2；随后精确恢复408源，初始/恢复/final同树。变异stale-generation用上一成功身份错误标记新代，只证明该错误被抓到，不称穷举所有错标。失败原件保留，均非超时/崩溃/编译失败。

**完整回归与独立审计。** 10:33:19–10:37:21+08:00，四Rebuild零警告错误，六dotnet/worker/launcher完整wait0，主线程完整回收exit0。16TRX Debug/Release各6103PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport4485/Acceptance521/Capture243/Sessions285。summary10602字节/SHA `03a7ca9cb227b337165b98bcb59974abd7744ea6b73a6e04d9fa3ebf33753764`。10:37:47–10:37:54独立审计原进程35812退出0、主线程wait0，报告206461字节/SHA `21bfa0fb4c3fc8daf439309587cb0cdbd75d49a4dbe7b5ab20cc88c795453970`：旧6065完整四字段Counter+38/-0，408源、157观察原件、39TRX/24992唯一execution，原件退出后再次保持。只直接核本片和固定6065基线，不递归重审全部历史；15旧ZIP只摘要保持，快照非连续锁、PID/wait非OS签名。

**提交与交付收据。** 代码`c730476f8f730f3f00e18f64c9bf15e29c87d4ce`，feat(m6): track successful full-frame sends by generation；验证文档提交6acd32e752f19dbef21907bd9dee5addb0f181d0。均普通push并实读完整号一致。10:47:05–10:47:18原打包进程16428完整wait0且主线程回收0，独占ZIP `outputs/LanRemote-M6-整帧发送成功边界-验证证据-20261016.zip`132554941字节/SHA `0b24a2c6f6af65bdeed9905f39dbd62ccca8f1b4a154147fed0acfa28971be8c`，589载荷+1清单=590条目，生成器逐项SHA256/CRC及包外输入核验通过。10:47:41主线程独立完整回读再次通过：408源/157原件/15旧ZIP保持，MEMORY9994字符。验收TXT4640字节/SHA `bded3112e7ef43a47b711c46c648943476da1d5ec81e13e7765af0af087eb9ad`；完整身份差集16390字节/SHA `fb981b08dc3b5c48b75314c32c3a428f8da1a4544db4d1fefd255ea8234bded0`；SHA收据1409字节/SHA `62c7f92814e48e94ef80a463f4e8a45adbc150eaf243a719984c07c1f885527d`。主线程预检曾发现记忆10093超10000并压缩后实测9994；发生于打包前，不是打包失败。包内文档保留打包时快照，本段后置三文件记账不重打ZIP；最终after-docs独立收据核精确提交差集和Git干净，不泛化放行任意文档变化。

**边界与下一片。** 本片为真实sender/source加受控Stream/producer的内部自动化，不是实际TLS、GDI、双机、真人或物理多屏验收；没有Control成功回执、Client显示屏障或产品live切屏。下一片具体请求/代际可靠关联与成功/停止结算，再Control回执/显示屏障/产品路径/画质。不重验M4/M5、不启M7、不改网/防护/权限/Undo、不覆盖旧ZIP，无M6产品包。

### 19.58 M6 请求绑定的代际首发结果与无成功结算（2026-10-16）

**起点与范围（ADR-102）。** 起点本地/远端bd81f3a236d4c3cf3b4c7dcb8db824512075b947一致，§19.57已实际交付。四生产修改DisplaySwitchExecutor/SessionRegistry/ReplaceableVideoFrameSource/VideoFrameSender，新增VideoSendSettlementTests。不改Core模型、wire格式、writer、Router/App默认路径或认证权限。本片验证通过并已提交e8969f3；以下合同不等于M6产品收口。

**逐代结算。** 每个真实代际持原FirstSend任务，仅记录显式settleSend追踪尝试的首个本地完整发送元数据或null。追踪读在原帧交付gate内登记在途，source退役同gate封口；封口且全部登记尝试均无成功才null。旧帧交付后即使source.Completion已退出也不能抢先null，仍等原writer及sender末检查后成功或失败结算。每次ReadOrigin以原子一次门禁结算，成功后的finally(null)不重复减计数；首个成功不被新帧或新代覆盖，不轮询单槽历史、无事件队列。

**请求绑定与原错误。** CreateTrackedExecutor显式启用句柄，不自动装配追踪sender。TryExecute第三out在接纳时给出原Request及Binding原任务；Registry→Reservation→Executor共享父gate，原Replace正常同步返回且候选owner移交后绑定其out代际，未移交finally绑定null。后续异步Start/退役失败仍可取已移交身份，但原执行任务继续以原错误树失败。成功ExecutionResult.SendReceipt引用同一请求/代际。旧CreateExecutor/两out入口仍可用、原默认结果不附句柄，拒绝不发句柄。

**无反向等待。** FirstSend/Binding是元数据结果，不是资源根或当前资格；source退役、source.Completion、执行器原Task及Completion均不等待FirstSend。发送失败但source未封口时允许结果继续等待，外层必须独立停止来源并完整join原sender/producer/执行器/物理close；不能拿取消通知或结果Task代替join。普通读和旧sender不登记，null不代表所有非追踪wire发送都没发生。调用方保留句柄才能读取该次历史；生产代码不缓存无界请求集合。

**确定性测试及初始结果。** 新17方法/51展开case。真实三段IO尾部×success/fault/cancel/deadline/closed、source先完整退出而旧发送仍在途、精确预算不触发timer、首次成功跨后帧/后代保持、零帧封口不反等Start/Read/退役、尝试幂等、旧入口不追踪、清理失败保历史；真实registry/lease/Admission到请求身份、父失效、准备异常、Stop前移交拒绝、Start/旧代清理异步失败保Binding。结果读取先在客观原根结束/受控边界断言IsCompletedSuccessfully，finally仅完整join真正资源根，不等待被变异破坏的纯结果TCS。首次及恢复定向各308PASS/Rebuild零警告错误，初始原进程链主线程回收；未发生本片编译或正常测试失败。主线程在首次验证前补5处async的Task显式丢弃，准备异常树断言采用原单root对象，不错误断言Task.Exception外包装。

**变异、全量与审计。** begin-attempt/seal-pending/successful-send/failed-attempt/request-binding分别21/18/7/15/3原断言失败，原build成功、test退出1、worker/launcher退出2，每次完整退出后精确恢复全部409源，不以编译失败/超时当kill。11:36:16–11:40:26+08:00四Rebuild零警告错误，六原dotnet/worker/launcher完整wait0；16TRX双配置各6154PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport4536/Acceptance521/Capture243/Sessions285。summary10551字节/SHA `3f589fef9d2d9e92aea630c5ddfafadcbb34c1ebe9de7183666fab6c9fa5865c`。主shell后台句柄本次查询无记录，原stdout与launcher链真实wait0仍在，不伪造主shell回收。11:50:42–11:50:49独立审计原进程35628完整wait0且主线程exit0，报告216956字节/SHA `f0be66575aa5617569ed63a46decc50bd5320577a70a1a076a46fd61bbbb39c0`；旧6103完整四字段Counter+51/-0，409源/159观察原件、39TRX/25385唯一execution、16旧ZIP摘要保持，退出后原件再次核对一致。只直接审本片与固定6103基线、不递归全历史；快照非连续锁、PID/wait非OS签名。

**提交与交付收据。** 代码`e8969f3de6453972f92aa8617322d0ec666dad17`，feat(m6): settle first sends for request-bound generations；5files/+1223/-21。验证文档c4d934c06484e77750e9bc75d380ca2a6afb31a7，均普通push且实读完整号一致。12:05:08–12:05:20原打包进程22068完整wait0并主线程回收0；新ZIP `outputs/LanRemote-M6-请求代际发送结算-验证证据-20261016.zip`132670278字节/SHA `2bcebba26ed24c038f0fedef5081c43aa59d0dbc5489f9859fbf588fadcdb616`，590载荷+1清单=591条目。生成器逐条SHA256/CRC及包外输入通过，12:06:09主线程独立全回读exit0：409源/159原件/16旧ZIP保持。验收TXT5350字节/SHA `dbfad72e299410bea543d5a0d4ea6eeee9e792371ed95264948986343073978c`；差集22394字节/SHA `0e63c34dfc3cf5aaab0eb7e5909ed57fc94fab4d831467973d36cb857333cf20`；SHA收据1415字节/SHA `945a10202fd41b24b598be7f22c9505797c3327885b7f4e24a5e42206d10090b`。打包前记忆长度预检10075/10021超限，压缩后9982通过，均未运行打包或提交，非构建/产品失败；没有覆盖原件。后置三文档只追加交付账，不重打包；after-docs终检按精确三路径提交和当前字节核对。

**边界与后续。** 单飞仍覆盖原执行生命周期，不扩张为“接纳至Control回执占槽”；Stop与回执资格没有新共享提交gate。下一片须定义回执合同、请求占槽到最终回执及父资格原子检查，再唯一Control读写接线、Client显示屏障、产品路径/画质。本片受控Task/Stream非TLS/GDI/双机/真人/物理多屏，不是完整Control成功ACK；M6未完成，无新产品包，不重验M4/M5、不启M7、不改网/防护/权限/旧ZIP。

### 19.59 M6 Control成功回执纯合同与请求占槽（2026-10-16）

**起点与范围（ADR-103）。** 起点本地/远端db92c203d772aaad35d9f53501e36013993c9080一致，§19.58四文件已展示。三生产修改DisplaySwitchExecutor/SessionRegistry/ReplaceableVideoFrameSource，新增DisplayChangedFrame及两个测试类。不改VideoFrameSender/Writer、Core、LRVF或认证权限；默认Router/App及原两个执行器模式保持。本片验证通过，代码d99a7e0已推；不将内部委托称作真实Control上线。

**纯回执。** display_changed严格11字段：type/protocol/sessionId/seq/timestampMs/requestSequence/catalogSequence/displayId/firstFrameId/width/height；Host seq与请求序号分离，frameId完整uint64，尺寸1..8192。4096字节预算、唯一完整字段、无未知/重复、深度1、类型精确、拒非规范GUID/非法UTF8/小数指数及尾JSON；只返回UTF8载荷，不触stream/clock。不定义失败回执或消费端显示屏障。

**占槽与真代际。** CreateReplyExecutor显式启用。原execute仍只prepare/Replace，但成功后原预约保持到reply原worker退出；HasActiveExecution同时约束在途reply，即使finally已释预约仍不可重入。仅私有PendingReply保有真实Request/Generation，TrySendReply拒伪造/跨执行器/旧receipt且不误释放；不相信外部可构造Binding值。只有原execute成功及其真实FirstSend非空可提交，回执首帧/尺寸由真历史生成；Host seq/time由后续唯一writer提供，本片不负责全连接序列分配。

**逻辑提交与完整join。** 先登记reply worker，worker再同registry gate核当前父/预约/lease/publication，并在source gate核active同代际且未关闭；等待source gate后再次查IsCurrent。最终复检通过为逻辑提交点，外部writer同步前缀仍在锁外；原Task完整等待，异常树不Flatten，Completion依次join原execute/reply/cancel。Stop先赢不调用writer；提交先赢后Stop可出现物理迟到字节，外层必须close/cancel并完整join，不能宣称撤回已提交IO。CurrentAtCompletion只为返回时快照、不授予后续资格。来源/视频sender不反等reply，无队列/自动重试/取消代理。

**测试与变异。** 新codec505case、生命周期33case，初始定向722PASS/Rebuild零警告错误。覆盖真实registry+lease+source/sender首发、保槽/Busy不消费序号、伪造Binding、原worker最终父/代际检查、Stop与阻塞同步writer/原Task/取消回调完整join、多根错误、旧模式、主动释预约隔离原reply Task门禁。六唯一生产变异duplicate-field/unsigned-frame/hold-reservation/worker-parent-check/original-reply-task/original-write-tree分别44/2/1/7/1/1原断言红且精确恢复；前两轮包装器误判xUnit自定义断言消息，原失败测试及恢复均真实。保留三个mutate版本与原轮，审计对这46失败绑定精确方法/参数/源码599或605断言行，不泛化把任意消息当Assert。正式初始/恢复均722PASS，源码保持；本片没有编译或正常测试失败。

**完整回归与审计。** 14:59:00–15:03:14+08:00四Rebuild零警告错误，六原dotnet/worker/launcher完整wait0、主线程完整回收；16TRX双配置各6692PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport5074/Acceptance521/Capture243/Sessions285。summary10448字节/SHA `3e157379313a83e7c76a2e530c6a1df2af9adce5a4332e4f2ddad1bc95e9addd`。15:04:37–15:04:46独立审计原进程11180完整wait0、外层及主线程exit0，报告473544字节/SHA `dd4f5a15beef4e98b55ec1a0fbf894be26b9f345be1db97a03ee183ad412ff31`，旧6154完整四字段Counter+538/-0，412源/171观察原件、40TRX/27291唯一execution；退出后原件再次核对保持。17旧ZIP只摘要前后保持；直接审本片与固定6154基线，不递归全部历史。快照非连续锁、PID/wait非OS签名。

**提交与交付收据。** 代码d99a7e0e8c779872d17e74ff041cd86a19bf7be9，feat(m6): gate display replies and hold request reservations；6files/+1692/-13。验证文档3fc81a544c2726b495713c86a0dd18565af2bfe8，均普通push并实读完整号一致。15:35:58–15:36:14打包原进程35532完整wait0、主线程回收0；新ZIP `outputs/LanRemote-M6-Control成功回执与占槽-验证证据-20261016.zip`133219747字节/SHA `d8ee62ed8fb66531d265b7e46e8f2adb539e240bb91fc014803cb9c725bc5168`，610载荷+1清单=611条目。生成器逐条SHA256/CRC及输入核验，15:37:19主线程独立全回读通过；412源/171原件/17旧ZIP保持。验收TXT5662字节/SHA `5abc4c432f2cc46426d3c1e0019deb432d97f11ff8bcdc21c65eb0b6eed56770`；差集246661字节/SHA `21e63ec44ac8dfc915b99c28947a8e3bfd824aa2967c0df7d9d360bcf4c72e59`；SHA收据1426字节/SHA `a65dafc7affe464bc1b73798f3ceeba1c884de52b87d8da8f032862da2183b9c`。记忆预检10078超限已压缩至9996，发生于提交/打包前，无打包失败。后置仅三文档交付账，不重打包；最终after-docs依精确三路径提交及原件保持核验。

**后续与限制。** 后续须将回执委托接入原唯一Control写者并纳入统一绝对预算、停止监督和全连接seq，再接唯一reader、Client显示屏障/产品live/画质。此片delegate必须自行覆盖序列化、原IO/Flush/预算，尚未装配实际网络；缺首发/父失效返回不可提交，由外层停止回收，不假造成功。测试为受控Task/Stream而非TLS/GDI/双机/真人，不重验M4/M5、不启M7、不改网/防护/权限/旧ZIP，无新M6产品包。

### 19.60 M6 显式认证后Control会话唯一写者（2026-10-16）

**起点与范围（ADR-104）。** 起点本地/远端b383368c219e8682a888dd508b5b4864e2a458ad一致，§19.59交付完成。原系统无通用会话writer，只有认证栈顺序写及单次目录独占约定；本片新增HostControlWriter/原holding监督、四生产修改ControlAuthSession/HostAuthenticatedCatalogLifetime/HostDisplayCatalogSendAttempt/DisplaySwitchExecutor，两新测试类。不把“原唯一写者原则”冒称已有会话对象。最终验证通过，代码7968725已推；默认App不启用新路径。

**真实可选装配。** options保持原唯一两参数构造，新增UseSessionWriter init默认false，true时ControlAuthSession仍在auth_success完整写/刷→Register→秘密finally→原holding之后构造同流writer，目录经writer写出后才Ready及实际重编号Catalog。失败或先停止Ready=null；已成功Ready仅历史入口。窄DisplaySwitchExecutor writer重载保真实请求/代际门禁，将成功回执送入同writer；模板Host seq/time被重建，request/catalog/display/firstFrame/width/height不变。没有新增reader，真实request_display输入仍未上线。

**序号和预算。** 单原Task持槽，Busy/无效参数/预取消同步拒绝无队列；接纳时预约long seq，失败不回收，MaxValue只用一次、下一次永久封口。序号仅覆盖本显式post-auth目录/回执，不覆盖Hello/认证握手。UTC/序列化和原source/Write/Flush/timer清理复用既有原绝对预算，接纳预约早于source前锚点；取消注册收尾独立完整join，不虚称其调度耗时在原budget内。目录source后先CheckStage，再UTC投影序列化，停止/到期先赢则迟到source不得再触UTC。source同步前缀及AsTask直接进底座，未知退出/未知wire借用不弱化。

**停止、归属与错误。** writer Abort→既有closeRequired→父撤销和关线，再join原writer/attempt/holding/video；不建新关闭链，不反等视频来源。原四类写来源/操作/预算/清理错误树逐槽聚合，纯无原故障停止只取消当次write，writer.Completion正常；真实source/IO OCE已聚合Faulted不豁免。Host取消在初次调用尚未返回写Task的窄窗口只按同host token豁免。Auth保存writer字段，owner建造/Start失败仍finally RequestStop及fallback join，错误通过原AddCatalogErrors owner-completion槽报告。

**初始定向与已知历史。** 初轮build退出0但5个xUnit1031警告，零警告门禁退出2且未运行测试；保留initial-v1原记录。仅将5处已Assert完成后的GetResult改为await，不改用例身份或生产源；initial-v2定向157PASS（新writer29+integration8=37），Rebuild零警告错误。真实V2认证/同一受控Control流目录seq0→真实registry/视频首发→回执seq1已测，默认false目录保原seq；无双机TLS/GDI/真人证明。首次全量Debug发现3个原Acceptance反射用例ParameterCountMismatch：可选构造第三参仍改变反射签名。未改旧测试，已恢复原唯一两参构造并改为UseSessionWriter init；初始v3在最终树重跑157通过，全部六变异及全量重新执行，旧轮和首次变异保留为历史。最终初始v3/恢复v2均157PASS；旧API不变且完整6692基线保留。

**最终变异/全量/独立审计。** 最终树六变异sequence-reuse/session-check/late-source-project/reply-adapter/source-error-tree/sequence-exhaustion分别1/1/2/1/1/1原断言红并恢复，全部build成功/test退出1/worker及launcher退出2，非编译失败/看门狗代替击杀。17:18:11–17:22:21+08:00四Rebuild零警告错误，六原dotnet/worker/launcher完整wait0且主线程回收；16TRX双配置各6729PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport5111/Acceptance521/Capture243/Sessions285。summary10467字节/SHA `4bb1dbc86423859a36d0841c38504dccd9e90e6b69e2941072dfa0d1d4de251c`。17:24:25–17:24:36独立原进程24200完整wait0、外层/主线程exit0，报告238419字节/SHA `61777582afc3f18513fb1f846573e61186cf35113d1434cc8f8111f01b4331a5`；旧6692完整四字段Counter+37/-0，415源/295观察原件、56TRX/34213唯一execution、18旧ZIP摘要保持。首组六变异及接口兼容修复以精确唯一逆字节桥核实，所有历史stage列明，不拿初版结果充最终。只审本片与固定6692基线，不递归全历史，快照非连续锁/PID非OS签名。

**提交与交付收据。** 代码7968725b264481a08e7a84ef5f83554ac4672c85，feat(m6): serialize post-auth control writes with owned budgets；7files/+1657/-21。验证文档ef4a51f9b1ecc58362cbb1504ac95fbf5c4bd182，均普通push并实读远端完整号一致。17:59:02–17:59:17原打包进程47736完整wait0且主线程回收；新ZIP `outputs/LanRemote-M6-Control会话唯一写者-验证证据-20261016.zip`135568293字节/SHA `174c84fd90db29fa4ee146f3b390c7ca64a26538aafa171c490e5e1222463f2f`，747载荷+1清单=748条目。生成器逐条SHA256/CRC核验及18:00:51主线程独立全回读通过，415源/295原件/18旧ZIP保持。验收TXT5760字节/SHA `0eba179cadd17551fcc37defe7fd28a06ae40ede4a1781da46668f06e990964c`；差集15988字节/SHA `b1d572e0f9029118c14915f41108fbcf02420402c4c116c1a2e2b9173a7e3fde`；SHA收据1416字节/SHA `e29a82e380ff8d7333681d63ca4b2a925db3a21ebd625b28bc30a2afce9f3eb6`。记忆长度10102已在打包前压缩至9998，未覆盖旧包。后置仅三文档收据记账，不重打包；最终after-docs按精确三路径提交与全部交付保持校验。

**下一片与限制。** 仍须装配Host唯一request_display reader、请求执行器与父停止完整协调，Client同一reader消费回执并建显示屏障，再产品live/画质。当前新模式只internal opt-in，默认App未启；唯一性是同会话装配约定，不能阻止任意内部代码绕过writer直接写stream。停止后不可撤回已提交字节；无产品包，不重验M4/M5、不启M7、不改网/防护/权限/旧ZIP。

### 19.61 M6 显式Host请求读取与单处理槽停止监督（2026-10-20）

**起点与边界（ADR-105）。** 本地/远端866d73ed1d0d6708f10b71d95de8cbfb0607e6d2一致且干净；§19.60四成品摘要再次保持。四生产修改ControlAuthSession/HostAuthenticatedCatalogLifetime/HostControlWriter/SessionRegistry，新增HostDisplayRequestReader及两个测试类。显式DisplayRequestHandler init仅在UseSessionWriter=true可绑定，默认null保持旧holding/App。此片接网络请求读取与可信内部handler原Task监督，不自动创建固定目标执行器，不接Client回执消费或产品live。

**唯一读与预算。** 审批期或原holding位置已经启动的首字节读完整移交为byte?，不重复Read，null=EOF、0仍为长度数据；后续只有ControlReadAhead一条读链。每帧4KiB、首字节本地消费观察的10秒原预算，ControlFrameReadAttempt包含原IO/timer退出，严格request_display/会话/JSON合同后再次用原锚点及频率高水位做解析末检。原Task结果交付后只清自己静止payload，不宣称TLS副本擦除。

**EOF与业务原根。** 一个Task.Run handler worker隔离外部同步前缀，唯一reader继续读取，不因prepare/回执等待而失去EOF监督。第二完整帧若原handler未退出则fail-close，无请求队列或未定义失败回执。处理失败、EOF或帧Abort先触closeRequired/StopRequired和独立取消worker，再join原读取/handler/取消回调。writerLifetime在目录前后都监听InputStopRequired，父注销不依赖reader全部根已退出，避免反向等待。已登记但未进入外部handler时Stop先赢允许零调用；通过检查后仍可能物理迟到。

**派发与错误槽。** Dispatcher在派发检查点要求父registration.CheckCurrent()、writer未停止、Ready成功和实际Catalog；检查失败立即关闭，不等待Ready、不缓存请求，不能宣称所有物理提前字节都必在Ready前被处理。真实切屏仍需后续handler装配走原admission/Replace资格。首读保存底层原Task多故障槽，仅显式移交消费完整错误，旧Activity仍保持原await异常形状；reader接管时JoinPending只完整join，不重复向Router报告同一首读根。reader.Error逐槽保原handler/IO/预算/取消异常树，经AddCatalogErrors唯一request-reader槽报告。

**测试/历史。** 核心37、认证集成14，共51新增；正式最终初始v5定向125PASS。首轮13个反射断言失败因捕获registration将原RunAsync字段移入闭包，独立CreateRequestDispatch恢复旧状态机布局，未改旧用例。v2/v3/v4为中间绿树，后续收窄合作取消同token、补首读兄弟异常/JoinOwned不重复报告及忙槽可观测断言。首轮handler-busy变异仅watchdog失败，明确不计kill；增强第二handler进入信号后，最终四变异read-limit/handler-busy/parent-dispatch/handler-tree均1个原Assert失败且恢复。首次全量还发现原登记属性白名单不允许新增IsCurrent快照属性，改为CheckCurrent()检查点方法，保持原两个只读属性合同和旧测试不动；最终树再次定向/变异/全量。全部中间轮保留为历史。

**最终验证与提交。** 最终初始v6/恢复v2定向各196PASS，四生产变异各1原Assert红恢复。11:12:43–11:16:54+08:00四Rebuild零警告错误，六dotnet/worker/launcher完整wait0、主线程回收；Debug/Release各6780PASS/0FAIL/0SKIP，原6729完整身份+51/-0（Transport5162，其余项目保持）。summary10474字节/SHA `103b3f7198f41be947472c9b4ed2afe3b5da3e76b7a176305935cc81417f898f`。11:17:49–11:17:56独立审计原进程57500/外层/主线程exit0，418源/312原件、58TRX/34950execution，报告396216字节/SHA `7b46af3cda3271e62260efdfe2a59395e2365a589c7c5e033f7afbad7f4f073d`；前片四交付当前摘要保持，不重开更早18ZIP、不递归历史。代码4bf84587d751ad3b470ad7c37d8aabeffdab7558已普通推送并核远端，7files/+1749/-9。证据包改为本轮变更源码与原日志/TRX/报告，不嵌套旧ZIP。

**交付收据。** 验证文档b99a2787ac235a9e4b7dcf5eb3e6d7ea55272881已普通推送。11:26:10–11:26:12原打包进程57124完整wait0，主线程独立全条目SHA256/CRC及包外原件复核通过：346条目、新ZIP10582386字节/SHA `136604a711400e66220b5470e6620f3b25f87d691f986b1ab2e0f2fc81f2d9d3`，路径`outputs/LanRemote-M6-Host请求读取与停止监督-验证证据-20261020.zip`。验收TXT4776字节/SHA `ea3398a376bc8c21f15c4eb8e4821b16309cab939ca42c9707ebb096ab876f14`，另附SHA256收据。旧四交付和418源保持，后置仅交付文档记账，不重打包。

**后续。** 继续将handler装为真实单会话DisplaySwitchExecutor协调器，准备/首发/回执与父停止整体join，然后Client回执消费/显示屏障、产品live/画质。当前Busy为fail-close，不声称支持排队或可恢复错误回执。无M6产品包，不重验M4/M5，不启动M7，不改网络/防护/权限/Undo或旧ZIP。

### 19.62 M6 显式每会话自动切屏协调与真实首发回执（2026-10-20）

**起点/范围。** 接手53320d27141c6a950804e912da689477e420d7fb，原6780身份基线。ADR-106：新增HostDisplayRequestCoordinator；Auth/options/Router实际自动接线，DisplaySwitchExecutor新增窄拥有写入错误归属接缝。只注入DisplayRequestPrepare即可自动执行请求准入/准备/换代/真实首帧/回执，不再要求调用者手动组装handler、admission、executor、sender。默认App不配置，旧handler模式与公开API不变。

**实际链路。** Auth成功后构造稳定协调owner，首请求才用已发送实际Catalog惰性创建原registration admission及reply executor；无活视频发布立即fail-close，无等待队列或重新尝试。Factory Router由prepare配置自动启用原replaceable路径，并使用tracked sender(显式firstId或0, source)。真实request reader仍持续读取，coordinator单次Task直到原execution及新generation非空FirstSend及原reply Task退出；首发编号/尺寸由真实视频发送结果构造，不信任请求填值。旧代首发不能满足新代；header/payload/Flush或末预算未接受时无成功回执。成功请求完成不终止会话owner，后续复用同admission/executor及writer/Catalog身份。

**父停止/错误。** closeRequired/Host token独立监督，不依赖reader取消回调完成；先协调器/executor封口、Auth父注销、视频source撤销，再join请求/reader/writer/video。FirstSend是结果promise，不以它替代source/sender原资源join；停止时不反等其自然完成。execution/reply故障经executor.Completion一次进入coordinator.Error，Auth只报request-coordinator槽。TrySendOwnedReply对已取得且Faulted写者原Task仍join，但由writer唯一报告其故障；Canceled、同步调用失败及独立取消回调不豁免。旧TrySendReply不变。

**测试与迭代。** 新核心20case、自动协议组合12case。真实Auth（审批开/关）+VideoHello/ACK+factory Router+请求读取+真实sender/回执；视频/回执Flush闸、源未发布、EOF与迟到候选、忙槽、零帧/失败不回执、原错误唯一报告及默认路径均有覆盖。初始v1构建成功但4个CS4014 warning触门禁未测，显式保存已由夹具持有sender Task后修复；v2十项新断言误将FrameProtocolException.Message当Reason，改新测试使用Reason，旧测试未改。v3/恢复v1定向各191PASS；最终四变异fault-owner/reply-frame-id/null-first-send/frames-router分别1/2/1/1原Assert红恢复。

**最终证据/提交。** 15:21:46–15:25:48+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，双配置各6812PASS/0FAIL/0SKIP（Transport5194）；原6780完整身份+32/-0，summary10579字节/SHA `179e72a308ee3e13876518f6c19bcf11ebf8bd2ed5e2a691ce349fe3c3b46114`。审计v1错误地要求修复前历史新测试与最终字节一致而失败，原报告/进程完整保留；v2仅要求正式/变异冻结，历史仅允许两新增测试变更，旧测试不得变。15:28:52–15:28:56原审计进程44180/外层/主线程exit0，报告272690字节/SHA `03ac968cab4092e8b6239c761ace80826da64e35520be8b464a82525fcdce971`；421源/176观察原件/39TRX/27762execution，前片三成品保持，不递归更早ZIP。代码b4055c68f7c632a525ee132283d704740dc6a150已普通push并核远端一致，7files/+2031/-9。

**交付收据。** 验证文档a52eb83d12cfdef233fabeb63e5366a85afd4a92已普通推送。15:37:54–15:37:56原打包进程52348/外层/主线程退出0；新包`outputs/LanRemote-M6-自动切屏协调与首发回执-验证证据-20261020.zip`含198条目，8224453字节/SHA `29e97eca67e24990c38ec6c245a6410ed9fdfdb5c2f04ac3fc742202c13669dc`。生成器及主线程独立全条目SHA256/CRC/包外原件回读通过，421源与前片三成品保持。验收TXT4823字节/SHA `3867b09af79b7f364c6f54eb18643a5354589b421e9518c9dc4b55e98d90bfad`，另附SHA收据。后置仅交付文档记账，不重打/覆盖证据包。

**限制与下一片。** 仅internal opt-in与受控流，不是实际TLS/GDI/双机/GUI切屏或远端画面接受证明。下一片Client同一reader消费display_changed并构建视频显示屏障，再默认产品路径和画质；无M6产品包，不重验M4/M5、不启M7、不改网/防护/权限或旧ZIP。

### 19.63 M6 Client真实请求发送与严格回执关联（2026-10-20）

**拆片依据/起点。** 3bc7cee/6812起，用户确认无需当前真机操作继续。代码核查发现Client无真实request sender/登记，只有Host测试Feed；不允许凭假Arm把回执当授权，因此ADR107本片先接真实发送+唯一reader回执关联，视频/解码/UI屏障另片，不声称此片已完成显示屏障。新增AuthenticatedControlSession.DisplaySwitch.cs与ClientDisplaySwitchTests；原ControlMonitor/ClientVideoLifetime/PublicVideo/父撤销点最小接线。

**合同。** EnableDisplaySwitching仅最终交付后、attach/monitor前internal唯一启用，默认App不变。RequestDisplayAsync在真实已交付视频/运行monitor/当前目录中登记目标，序号0开始、Max一次、busy不消费无队列。4KiB/10秒payload发送复用原预算IO链，UTC/序列化包含在内。唯一monitor原目录读取256KiB/10秒末检保持，显式模式接受严格4KiB display_changed；无pending/错session/request/catalog/display/hostseq/重复拒绝。目录冻结实际对象，pending或已有回执后同值新目录也拒，默认降序目录兼容不动。

**早到/收尾。** 早ACK只放一个候选，不等发送而阻挡EOF；完整Write/Flush/timer成功后仍只局部candidate，caller注册退出后父gate复核才发布LatestDisplayChange。新请求清旧Latest；父/child/caller停止立即清查询资格并纳入原CloseAndJoin/evidence，原IO阻塞时不报退出。Latest仅已关联回执下界元数据，历史对象不可回收，不是UI/帧许可；没有新增ACK空闲超时，调用方取消/EOF停止。

**开发历史。** 静态审查修正初版发布早于caller registration Dispose问题；首轮新测试9处Task返回函数误选Assert.Throws(Func<Task>)产生18error及2warning，改显式Action保持同步拒绝合同。v2全部35失败来自夹具写错video_hello，实际channel_hello；新测试常量修正，生产协议和旧测试不改。ProbeTimer记录Change真实due而非初始Infinite。上述失败及中间轮原件保留，不作最终绿证据。

**最终定向/变异。** 新增35case，最终v3/恢复v1定向各208/208。四生产mutation request-sequence/write-budget/reply-correlation/operation-error分别1/1/3/1个原Assert红并恢复。custom Assert.False自定义消息不含字面Assert，误触driver的assert检查而中断；独立audit以固定源码第1016行、原栈和原消息认证是真实Assert，不靠修改测试或伪造timeout补证，不将driver中断洗成成功。

**最终全量/提交。** 16:54:54–16:59:02+08:00四Rebuild零warning/error，六dotnet及worker/launcher完整wait0；Debug、Release各6847PASS/0FAIL/0SKIP，Transport5229，其余原项目数不变。summary10471字节/SHA `6e274bff21fd02ce8c05c234bfe8240bfb9a6385c334b23e98fbe8bbb0555450`。代码`842a53ffd34b0f90201565fc59e7b84e0943f98a`已push并核remote一致，6files/+1578/-8。

**独立审计。** audit-v1因outer receipt没有log字段触KeyError；原回执是合法旧格式，失败记录原样保留。audit-v2不再无条件索引log，但日志仍必须逐行等于launcher；log字段有则继续核SHA，并未放宽真实日志/退出链。17:00:49–17:00:53+08:00原审计进程31532/外层/主线程exit0，report341948字节/SHA `1b016873c74014e2dbb13deafb7b600883b0a9c3200ea2de2b6344a7c51fcf64`。423source/175observed/39TRX/27948execution，旧6812完整四字段身份+35/-0，前片3deliverable保持。

**限制与后续。** 本片仅真实sender与严格回执关联，必须先有真实请求pending，不能fake Arm；没有新增public API，默认App未启，仍未接视频/解码/UI屏障。下一片视频帧封套携session/请求epoch/FrameId贯穿队列和JPEG解码，回执先/后都能重新评估单最新候选，旧frame/decode/Dispatcher不得挤掉或覆盖新代，最终Image.Source前同父gate复检；回执F为下界而非承诺F实际显示。完成内部展示接线后才开产品路径与真机双端，不将本片当真机显示验收；M4/M5不重验、不启M7、不改网络/防护/权限/Undo或旧ZIP。

### 19.64 M6 内部视频/JPEG/WPF显示提交屏障（2026-10-20）

**起点/范围（ADR108）。** a89fbab/6847继续。新增Transport的AuthenticatedControlSession.DisplayPresentation和App FencedVideoPresenter，内部三参RemoteVideoPresenter构造绑定真实已交付child；默认public两参构造/服务装配不变。新增Acceptance DisplayPresentationTests实际贯穿请求发送、原唯一Control reader回执、LRVF完整读取、baseline JPEG解码与STA Image.Source，不用fake Arm或测试构造stamp冒充成功。fixture合成已认证parent，非TLS/serverProof/审批或双机验收。

**帧与请求身份。** 只能在首次read/请求前绑定一次且须已启用切屏；绑定后旧public ReadFrame入口拒绝，专用读取仍用child原单读槽及完整projection worker。真实读取终检短区间签发携私有owner/epoch的stamp，跨整个视频严格ulong递增，0/高位/Max均保值，不加一/转long。真实请求接纳才换epoch，原发送/取消注册完整退出并发布关联回执才解除pending；父/child/呈现停止同父gate立即失效。

**有界实际呈现。** 新呈现器三个原worker（read/decode/render），单encoded和单冻结bitmap槽，同父gate交换、高水位与epoch防旧帧挤新，外部Dispose/解码/取消不在锁内。回执首帧F作为下界，实际帧>=F且实际解码尺寸相符才可提交；不要求精确F保留。帧先于回执时暂停呈现但保留最新候选，回执信号独立唤醒，不靠下一帧。只有一个原Dispatcher操作在途并由renderer完整await，提交前同父gate复检并写Image.Source，setter重入后再复检才记录LastPresented；旧回调不清新槽。LastPresented是逻辑提交历史，非物理scanout/远端授权。

**停止。** 先短gate封口/撤销/唤醒，独立请求父Control/Video关闭及取消，随后收尾原reader、真实JPEG同步前缀、原UI任务和owner释放。Stop不硬中断同步解码/Dispatcher，不把停止信号当退出，原UI受闸时完整join保持等待。App Stop及释放证据委派新实现，默认旧模式保持。Transport友元新增App/Acceptance.Tests，App友元Acceptance.Tests，未加public API。

**开发验证历史。** 初始v1构建成功但新测试2个CS4014和2个xUnit共4warning触门禁，未测；修正为显式持有原Task/集合断言。v2定向34/35，唯一失败是在活动发送WhenAny阶段错误强求直接await原Flush→request join链导致watchdog；改为原Flush受闸+request未退出/无呈现+放行成功的行为断言，原停止join另测，非放宽生产。v3定向35PASS，新增22case，原13默认呈现测试保持。首轮frame-floor/pending-gate/receipt-size/monotonic-id四变异1/1/1/2原Assert红恢复，仅作history非最终。final-v1 Debug通过、Release早ACK用例1FAIL，发现新fixture的fixtureEntered先于LastFlush引用发布，可误读初始CompletedTask。只改新fixture为FlushPublished TCS<Task>交付真实原Task引用，生产未改，然后完整重跑最终定向/四变异/恢复/双配置全量；失败记录、首轮变异和旧原件均保留，不替代最终同源证据。

**最终定向/变异。** initial-v4/restored-v2各35PASS；新增22全部Acceptance，原13默认呈现测试保持。四最终mutation frame-floor/pending-gate/receipt-size/monotonic-id分别1/1/1/2原Assert红恢复；上述首轮同名mutation单列history，不混作最终轮。

**最终全量/提交。** final-v2于19:20:23–19:24:28+08:00完成四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6869PASS/0FAIL/0SKIP（Acceptance543、Transport5229，其它项目不变）。summary10453字节/SHA `aed523d3963a82a861514d7af2420398ffcfd9f080c830e16df12e071859a41b`。代码`7757298396686201a272cb61e6f61bbb855d53bc`已普通push并核remote一致，8files/+1731/-5。

**独立审计。** 19:24:59–19:25:05+08:00原audit进程60052/外层/主线程exit0；report394170字节/SHA `1c5ca69d747b29969c285797a0e116613ef7f52911d475066d84ffdbe63c7993`。426源/285观察原件/61TRX/41361execution；旧6847完整四字段身份+22/-0，前片三成品保持。验证文档4f49d3a已推；19:41:27–28原打包33924/外层exit0，307条目12081371字节/SHA `d3473073317c6a833f0c24702e78445d0d244972b23fcc954c23ebb0dbd64bc0`，生成器和主线程全条目SHA/CRC及包外原件回读通过。验收TXT5690字节/SHA `bdece98f64989ad773c1db9cc191ad9f0d2f70c05da772e69b3c9a5d7965224e`，ZIP/TXT/SHA三成品已交付，不回写旧包。

**后续与限制。** 内部已贯通真实视频/JPEG/STA Image.Source逻辑提交屏障，不再仅receipt元数据；LastPresented非物理scanout或未来授权。默认App/屏幕选择交互与Host prepare仍未打开，不是双机验收。下一片不只是按钮：GdiScreenCaptureBackend当前只主屏，须补实际非主屏枚举/采集及Sessions目标准备到App的接缝；Host启用session writer/prepare，Client认证交付后attach/monitor前enable并用三参Presenter，窗口补真实请求/忙状态/停止解绑；目录仍每连接单发、无动态刷新。然后真实Host/Client联调、每会话画质和M6双端验收；当前无需人工前置，后续实机验收保留。不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧包，内部Image.Source成功不证明物理像素已scanout或两机已完成验收。

### 19.65 M6 内部显式非主屏GDI采集（2026-10-21）

**起点/范围（ADR109）。** 4f49d3a/6869继续，先补产品多屏的真实采集底座。仅修改GdiScreenCaptureBackend与GdiDisplayCatalog，新增GdiMultiDisplayCaptureTests和GdiMultiDisplayOffscreenTests；原IGdiNative、P/Invoke、App默认装配、Sessions固定目标工厂及其它生产代码不变；全量暴露一项旧Transport测试缺派发同步，后置仅补一行等待，全部原断言/身份保留并做字节逆桥。internal CreateMultiDisplay两个工厂显式启用，原public无参/internal单参仍旧主屏专用，构造不执行枚举或像素I/O。

**选屏与快照。** 目录提取internal只读完整DisplayObservation稳定快照，保留两遍原始顺序、256总项限制、非镜像附着过滤、唯一主屏/名称、有效几何及全部身份模式字段。多屏GetDisplays可列超帧预算元数据；Capture只对所选目标校验1..8192与16,777,216像素，在DC/DIB/租用前拒绝，其他大屏不误阻合法目标。以当前原始枚举索引匹配目标，不用过滤后位置，不缓存跨调用旧ID映射；缺失/脱离/镜像目标拒绝无主屏回退。

**共享原采集/清理。** 所选DeviceName创建设备DC，BitBlt仍设备本地0,0；负虚拟桌面X/Y只元数据，不混作源偏移。复用原top-down BGRA、alpha255、Flush后复制、全池数组清零归还、原Task单飞和取消等待、独立清理及原错误树。所有native清理成功后再次双遍观察，完整有序记录与采前一致才交付；包括目标或其他附着屏的名称/ID/key/几何/主屏/方向/bpp/频率/panning/状态改变、增删换序均拒帧并归还buffer。

**测试覆盖与变异历史。** 新53case（合成native45，真实offscreen离屏DIB8）。离屏source按所选设备名生成不同尺寸/图案，目标DIB/BitBlt/Flush/Copy/清理走真实GDI；包括合成副屏负坐标/竖屏、交替目标、创建与复制失败及重试，不冒充真实副屏。默认主屏旧测试及新对照都保留。首轮定向231PASS/零warning/error。第一次post-snapshot变异注入漏加null抑制造成CS8602告警，warning gate阻止测试；原源码已精确恢复，明确不是kill。修正仅变异replacement为snapshot!.Count，以post-snapshot-final重新执行该项及后续链，原warning阶段/receipt日志保留。

**唯一旧测试同步例外。** 首轮全量final-v1 Debug6922PASS，Release旧HostDisplayRequestReaderTests.IdleReadAheadHasNoTimerAndStopJoinsItsOriginalRead在line277原Assert.Single因Requests为空失败。原handler独立Task且held预读分支绕过BeforeIdleEof，handler与heldEntered无happens-before，Reached不保证Requests已入队；只在该旧测试Assert.Single前增加`await f.Dispatched.Task.WaitAsync(Guard);`，54字节。原断言/测试身份保留，Transport生产不改。审计按唯一完整方法上下文逆去这54字节即固定OLD原hash，不笼统声称所有旧test字节不变；本片Capture生产和测试在正式验证中真正冻结。修正后完整重跑，失败轮原样保留。

**最终定向/变异。** 三轮定向各231PASS；四正式mutation target-dc/post-snapshot-final/pre-stability/pixel-budget分别3/21/1/1个原Assert红且精确恢复。原post-snapshot仅CS8602 warning gate未测试，单列失败历史，不混作正式kill。

**最终全量/提交。** final-v2于11:22:49–11:26:54+08:00完成四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6922PASS/0FAIL/0SKIP：Core197/Security66/Protocol303/Integration3/Transport5229/Acceptance543/Capture296/Sessions285。summary10518字节/SHA `fd1d4036b66f7963cb61c6c880ab8003f4c5681120d543dd05edb3f7742b8b71`。代码`1028888b768b351384e239afd1d6f86b0a4efad5`已普通push并核remote一致，5files/+1389/-19（2production修改、2新增test、1旧test单同步行）。本次仅回填三文档，未重新运行dotnet、未提交，不将文档编辑计作代码验证。

**独立审计。** audit-v1因Gdi substring跨项目匹配，误把Acceptance的一项测试计入Capture定向而失败；旧失败原件保留。audit-v2按原argv的project/storage边界正确筛选，不放宽项目身份。11:30:13–11:30:17+08:00原审计进程4552/外层/主线程exit0，report1177362字节/SHA `805e61ef3de63184677a5862d2300825e62cc28368dcbed7d10b7a5624dd06d6`；428源/214原件/55TRX/42151execution，旧6869完整身份+53/-0。身份保持与字节冻结分开表述，上述唯一旧测试54字节同步例外通过固定hash唯一逆变换认证。

**限制与下一步。** 双遍/采前采后一致仅检测实际观察到的变化，不保证原子拓扑、ABA检测或未来设备身份；原索引不是持久授权，工厂prepare与捕获间不存在跨调用拓扑锁。internal CreateMultiDisplay已具备显式多屏枚举/采集能力，不再将后端笼统称为无副屏能力；默认primary API和App仍走旧主屏路径。本片未读真实副屏，offscreen不等于真实副屏/混合DPI/热插拔或双机验收。下一片为App接通Capture/Sessions internal friend与固定目标准备，Host按每个真实认证会话装配prepare/writer，禁止共享lastSessionId或临时Guid冒充认证身份；若接口缺session须内部传递真实身份。Client认证最终交付后、attach/monitor前enable并用三参Presenter，窗口接真实请求/busy/停止与请求解绑。冻结settings，目录仍每连接单发、无动态刷新；随后产品联调、每会话画质和M6双端验收，当前无需人工前置。无M6产品包，不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP。

### 19.66 M6 产品侧切屏接线（2026-10-21）

**起点/范围（ADR110）。** e82ccb73/6922继续，把上一片备好的多屏采集与目标准备接进产品链路。本片仅修改9个生产文件并新增两个Acceptance测试类；不改网络、防护、权限、Undo或旧ZIP，不重验M4/M5、不启M7，冻结预览settings，目录仍每连接单发、无动态刷新。App接通Capture/Sessions internal friend（两个csproj的InternalsVisibleTo）后使用多屏GDI后端与固定目标准备工厂；Host按每个真实认证会话装配prepare与同一会话写者；Client在认证最终交付后、attach/monitor前enable并用三参屏障Presenter；窗口目录项接真实请求、忙状态与停止解绑。

**会话感知目标准备。** HostDisplayCatalogSendOptions新增internal SessionDisplayRequestPrepare(Guid, DisplayInfo, CancellationToken)，与DisplayRequestHandler互斥且只能装配一次；ControlAuthContext.WithSessionDisplayRequestPrepare要求先有目录来源并以UseSessionWriter=true重建选项，不共享可变态。ControlAuthSession.BindCatalogLifetime把会话写者强制纳入校验；协调器准备闭包以登记表真实SessionId的**快照值**包装（Guid preparedSessionId = registration!.SessionId），闭包不持有登记对象本身——首轮实现直接捕获registration，编译器把该局部提升到闭包类而非异步状态机，25个旧Transport测试反射读取<registration>5__N的断言全部失效；改为快照取值后旧测试零改动恢复，语义上也在装配时即绑定真实身份，禁止共享lastSessionId或临时Guid冒充认证身份。FirstFrameRouter以统一IsCoordinated检测两种准备工厂，sender选择与构造校验一致。

**Client与窗口。** ClientConnectService在connect返回后、AttachVideoAsync前调用EnableDisplaySwitching，随后用view.StartDisplaySwitching(RequestDisplaySwitch, stop)把窗口请求接到同一Control会话的真实登记、发送与回执等待，最后以三参RemoteVideoPresenter绑定请求代际屏障。RemoteViewWindow目录项新增切换按钮，XAML默认禁用，绑定后才刷新启用；请求核心统一门禁（已停止/关闭中/会话已结束/忙/未绑定/取消/目标不在当前目录快照）并分类状态文本（回执/未完成/已取消/被拒绝/失败）；StopDisplayCatalog与SessionEnded和目录同一时机解绑切屏并清空stop与回调字段，DisconnectHandlerReleased纳入解绑证据。

**测试与定向。** 新增18项：ProductDisplaySwitchLoopbackTests 2项（真实Control TLS回环，验证回执DisplayId与尺寸、回执及准备工厂的SessionId都等于本Host实际登记的认证会话身份、切屏后呈现新一代红帧且50轮空转不被旧代覆盖、目录外请求在发送前被拒且不结束会话、忙时第二请求不叠加发送）与ProductDisplaySwitchWindowTests 16项（STA窗口交互生命周期）。首轮及两次恢复定向各45 PASS / 0 FAIL / 0 SKIP。

**受控旧测试两行例外。** 产品目录状态文本由“只读目录”改为“可请求切换屏幕（回执不等同物理输出）”，旧ProductRealViewOnlyLoopbackTests的两条Assert.Contains期望随之更新；只替换两个期望子串，测试身份、断言形式与目标表达式不变，完整两行块唯一逆替换逐字恢复固定OLD原hash（34204字节 / SHA `a62f618aecc0f850d4c6d94920e387c21040806a6ffbc1c98b0c613d05c21353`）。审计仅对该路径做old→new规范化比较，其余测试与生产严格逐字冻结。

**变异与未采用项。** 四项正式变异session-identity（准备工厂收到伪造Guid.Empty身份）/ busy-flag（忙标志永不置位）/ unbind-flag（停止解绑标志不置位）/ client-target（窗口真实目标不透传，固定default）分别1 / 1 / 1 / 1个原Assert红，原进程退出后精确恢复。HostRuntimeService的多屏后端选择未设变异：本机单显示且无任何测试经HostRuntimeService执行真实切屏，改回主屏后端无可观测差异、无测试可捕获，该选择已在§19.65以四项变异验证。移除EnableDisplaySwitching调用的变异未采用：生产守卫BindDisplayPresentation会先抛InvalidOperationException，属Guard而非测试原Assert，不能作为kill；该启用时序由回环Fact A全链（启用→附着→监控→绑定→回执）覆盖。

**首轮与抖动轮处理。** 首轮final-v1暴露上述25个Transport状态机字段失败（已按上文修复）。修复后重跑的两轮final-v1另有旧DisplayPresentationTests.EarlyAck与HostDisplayRequestReaderTests.StopBefore…在整仓并行负载下抖动失败；两者单独复跑、Acceptance整项目复跑、以及排除本片新增测试后的整仓复跑均全绿，本片未改其逻辑。该两轮源码与被修复后的final不同源，且审计history形态仅支持构建门禁失败（无TRX），故不保留为历史stage，连同首轮缺陷轮一并删除后整体重跑；此事实在此显式记录，不隐藏。

**最终全量/提交。** final-v1于17:11:59–17:16:30 +08:00完成四次Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6940 PASS / 0 FAIL / 0 SKIP：Core197 / Security66 / Protocol303 / Integration3 / Transport5229 / Acceptance561 / Capture296 / Sessions285。旧6922项完整四字段身份保留，新增18项，删除0。summary 10626字节 / SHA `1c6094be1365b3c4982edc5be65420ac08a20c6d79ab739589034a62648aff63`。代码`93d72e2027a257157628b31633b3cebc88971038`已提交，13files / +227 / -28（9生产修改、2新增test、1旧test两行受控更新）；推送时origin `https://github.com/Nuyoah-sir/LanRemote.git` 三次均报schannel TLS握手失败，本地提交与远端未核一致，待网络恢复后普通push（见“未完成/阻断”）。

**独立审计。** audit-v1因旧测试逆变换针脚漏写行首16空格缩进，唯一匹配校验失败；原失败报告保留，不覆盖。audit-v2于17:18:28–17:18:33 +08:00原审计进程59376 / 外层 / 主线程exit0；report.json 1001213字节 / SHA `3b8cf265d0db882ea973c59583c0107f87ba90ed63ccd12f50c0141832026f4b`；430源 / 168观察原件 / 39TRX / 27863唯一execution，旧6922完整身份 +18 / -0，前片三份成品（非主屏GDI ZIP/TXT/SHA）摘要保持。身份保持与字节冻结分开表述，上述唯一旧测试两行例外通过固定hash唯一逆变换认证。

**限制与下一步。** 本片只是产品侧接线：真实TLS回环、协调器与窗口已在进程内贯通，但不等同双机TLS联调、物理scanout或真人验收；回执仍是逻辑下界而非承诺实际显示。默认产品未变更公开行为，目录仍每连接单发、无动态刷新，预览settings冻结。下一步为真实Host/Client产品联调、每会话画质与M6真机双端验收；当前无需人工前置。无M6产品包；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP。

**推送。** 2026-10-21当次`git push`三次均 `schannel: failed to receive handshake, SSL/TLS connection failed`（含沙箱外重试），非仓库或凭据配置问题；2026-10-22网络恢复后普通push，代码93d72e2与文档1dceab5一并推送，`git fetch`后 `git rev-parse HEAD` 与 `origin/main` 均为 `1dceab5d19b0add184e5d6acbf7de8ed7fc773a1`，远端一致已核对。

### 19.67 M6 每会话画质底座（2026-10-22）

**起点/范围（ADR111）。** 93d72e2/6940继续（网络恢复后已于2026-10-22普通push并核remote一致）。原始任务M6 DoD含“每个Session画质独立”，而当前画质是工厂/主机级单一设置（HostRuntimeService以previewSettings建厂，运行中禁止更换），与DoD不符。本片只补Sessions层底座：仅修改FramePipelineVideoProducerFactory并新增一个Sessions测试类；不改协议、不改App/Host默认行为，冻结settings与目录每连接单发不变。

**按会话画质入口。** 新增两个internal入口CreateWithSessionQualityAsync与CreateForDisplayWithSessionQualityAsync，画质只作用于本会话管线：既不改写工厂默认档位，也不影响其他会话已持有的设置，会话结束后随管线释放。既有两个准备入口（CreateAsync、CreateForDisplayAsync）的形态契约、默认档位与调用方不变。

**为何另立入口而非重载。** 首轮为两个既有入口增加带画质重载，旧测试FramePipelineVideoProducerFactoryTests.Target_display_preparation_remains_internal_without_expanding_public_factory_contract用反射取方法时得到AmbiguousMatchException（重载歧义）。旧测试不可改，因此改为独立命名入口，形态契约与既有反射断言都不受影响；该缺陷轮不保留，改名后完整重跑定向、四项变异、两次恢复定向与双配置全量。

**校验顺序。** 画质在任何委托调用、显示器枚举与像素预算之前由FramePipeline.ValidateSettings校验：非5/10/15/20/30 FPS、非50/67/75/100%缩放、JPEG越界40..85或Auto=true均同步拒绝，不创建采集后端或编码器，也没有需要清理的依赖（旧路径是先创建编码器再在管线构造里校验，靠catch清理）。空会话标识仍先于画质校验，空画质按参数空值拒绝。

**测试与变异。** 新增11项：按会话画质到达本会话管线且不改写工厂默认；两会话各自持有画质并真实编码到各自设置；五类非法画质在创建依赖前被拒；空会话标识与空画质均不创建依赖；固定目标与会话画质同时生效；固定目标配非法画质同样前置拒绝。首轮及两次恢复定向各296 PASS / 0 FAIL / 0 SKIP。四项正式变异quality-ignored（忽略会话画质改用工厂档位）/ quality-late-check（取消前置校验、依赖先创建）/ display-ignored（固定目标被忽略）/ null-quality（取消画质空值拒绝）分别1 / 5 / 1 / 1个原Assert红并精确恢复。

**最终全量/提交。** final-v1于2026-10-22 10:11:29–10:15:50 +08:00完成四次Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6951 PASS / 0 FAIL / 0 SKIP：Core197 / Security66 / Protocol303 / Integration3 / Transport5229 / Acceptance561 / Capture296 / Sessions296。旧6940项完整四字段身份保留，新增11项，删除0；本片未修改任何旧测试，无同步修正桥。代码`32420e4dce4f1391b545f7198a51af065996a8ee`已提交，2files / +262 / -6。

**独立审计与打包。** audit-v1旧报告路径误写report-v2.json、audit-v2 runner固定字节仍指向上片runner，两轮原报告保留不覆盖；audit-v3于10:18:34–10:18:39 +08:00原进程42944 / 外层 / 主线程exit0。report.json 906345字节 / SHA `a4f2cd1d8199cbcf80c8dba94c89f1a84baba7693541878639b088026856ae0c`；431源 / 165观察原件 / 39TRX / 28678唯一execution，前片三份成品摘要保持。打包175条目8756470字节 / SHA `e8ba324156aaf37bb5885f34b7bda46ed172d97936f82f8aef60c7061727b305`，全条目SHA/CRC及包外四份原件回读通过。

**限制与下一步。** 本片只是底座：App/Host尚未按会话分配画质，产品默认仍走工厂档位，不等于每会话画质已在产品生效。下一步把Host当前画质快照到每个认证会话（会话创建时固定、运行中互不影响），再接scale/quality presets、ping RTT、actual FPS与auto quality简单控制器，然后真实Host/Client产品联调与M6真机双端验收；当前无需人工前置。无M6产品包；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP。

### 19.68 M6 每会话画质接线（2026-10-22）

**起点/范围（ADR112）。** 32420e4/6951继续。上一片只备好Sessions底座（可按会话指定画质），但Transport只按sessionId调CreateAsync且不带画质，产品仍表现为主机级单一档位。本片把画质按真实认证会话分配：工厂新增可选按会话画质解析回调；App新增SessionQualityResolver做快照与清理；不改协议、不改既有准备入口的形态契约与默认档位。

**解析与校验顺序。** 回调返回值非空时该会话用它、为空时回退工厂默认；回调本身与画质都在任何委托调用、显示器枚举与像素预算之前处理：回调抛出或返回非法档位都同步拒绝，不创建采集后端或编码器。回调只读主机当前档位属性，不捕获StartAsync的启动参数局部——上片已证明捕获局部会让编译器把该局部提升到闭包类、破坏旧测试对状态机字段的反射读取。

**快照而非现读。** 会话首次解析时把主机当前档位快照入表，之后主机换档不影响该会话；显式设定（Set）优先级更高，非法档位与空会话标识都拒绝且不改写该会话已持有值；主机停止（CompleteDrainAsync）时整体清空，不跨主机生命周期保留。主机未指定档位时返回null并回退工厂默认，也不落表，避免把“无独立档位”记成“有档位”。

**测试与变异。** 新增10项：工厂回调4项（只作用于本会话、null回退工厂默认、非法回调画质与回调异常都在创建依赖前被拒、无回调时默认不变）；解析器6项（首次快照且后续主机换档不影响、两会话各自独立、当前为null时不落表、Set覆盖与非法/空输入拒绝、Clear后重新快照）。首轮及两次恢复定向各35 PASS / 0 FAIL / 0 SKIP。四项正式变异callback-ignored / callback-late-check / resolver-no-snapshot / resolver-set-no-validate分别1 / 2 / 1 / 1个原Assert红并精确恢复。

**最终全量/提交。** final-v1于2026-10-22 10:49:39–10:53:49 +08:00完成四次Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6961 PASS / 0 FAIL / 0 SKIP：Core197 / Security66 / Protocol303 / Integration3 / Transport5229 / Acceptance571 / Capture296 / Sessions296。旧6951项完整四字段身份保留，新增10项，删除0；本片未修改任何旧测试，无同步修正桥。代码`e250ca2c26872c89c0002d79cdb30518fa02a38d`已提交，4files / +286 / -4。

**独立审计与打包。** audit-v1于10:54:46–10:54:50 +08:00原进程43708 / 外层 / 主线程exit0；report.json 909769字节 / SHA `849184bad9a6075481c0686324e9fd37adf1c052e79524ce366fac5006adab3e`；433源 / 166观察原件 / 39TRX / 27934唯一execution，前片三份成品摘要保持。打包177条目8593357字节 / SHA `534da7bcd2bd2c189052f70c7fa70fdc03130128b6f726701ea1357b02344a68`，全条目SHA/CRC及包外四份原件回读通过。

**限制与下一步。** 画质已可按会话分配，但产品侧尚无UI或协议来源为特定会话改档，当前仍表现为主机档位；未接scale/quality presets、ping RTT、actual FPS与auto quality简单控制器。定向只覆盖Acceptance.Tests，Sessions.Tests既有工厂/管线测试由双配置全量覆盖；未对固定目标路径的回调解析单设变异。下一步接scale与quality presets、ping RTT、actual FPS与auto quality简单控制器，然后真实Host/Client产品联调与M6真机双端验收；当前无需人工前置。无M6产品包；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP。

### 19.69 M6 实际帧率指标（2026-10-23）

**起点/范围（ADR113）。** e250ca2/6961继续。M6清单要求actual FPS作为auto quality的输入，此前FramePipeline只暴露目标帧率相关设置，没有任何“实际编码了多少帧、实际多少帧每秒”的指标。本片只补指标：仅修改FramePipeline与FramePipelineVideoProducer两个生产文件并新增一个Sessions测试类；不改协议、不改App/Host默认行为，不接控制器，冻结settings与目录每连接单发不变。

**为何用Stopwatch而非注入时钟。** 既有测试断言FramePipeline构造/创建阶段`TimestampCalls==0`，并有7项注入时钟失败场景要求保留原异常；在构造或Start里新增`TimeProvider.GetTimestamp()`会让这些旧测试失败。因此计数与起点都用`Stopwatch`：`_startedTicks`只在真实`Start()`里取一次，`EncodedFrames`只对真正被编码队列接受的帧`Interlocked.Increment`，`ActualFps`=帧数/经过秒数，未启动或零帧一律为0。指标不因读取而清空，也不声称等于目标帧率、显示帧率或网络发送帧率。

**断言口径。** `EncodedFrames`用精确断言（0帧为0、4帧为4）；`ActualFps`只写弱断言（0帧为0、有帧后>0、重复读取不清空历史），不把不可确定的值写成精确期望。另有两条管线各自计数、互不共享的用例，验证指标按会话/管线独立而非静态共享。

**两处脚本缺陷轮（显式记录，产物已删后完整重跑）。** ①首轮counter变异把`Interlocked.Increment`替换为注释，编译器给出CS0649“字段从未赋值”告警，被runner零警告门禁拦下——告警门禁不是测试红，不能算kill，改为`_encodedFrames = 0;`的赋值写法后正常。②xunit v3的`Assert.True(condition, message)`在TRX里只上报自定义消息、不含“Assert.”前缀，原audit的`"Assert." in message`判定误拒真kill；改为「消息含Assert.或堆栈落在本片新增测试类」双重认证，并仍禁止Timeout/Guard/Watchdog/超时替代。两轮缺陷产物均删除，随后完整重跑initial、五项变异、两次恢复定向与双配置全量。

**测试与变异。** 新增4项：未启动管线报0帧0帧率、已启动但零帧仍报0帧率、有帧后帧率>0且重复读取不清空、两条管线各自计数互不共享。首轮及两次恢复定向各4 PASS / 0 FAIL / 0 SKIP。五正式变异counter-not-incremented（计数不上增）/ fps-always-zero（帧率恒0）/ start-tick-not-recorded（启动时刻不记录）/ fps-resets-history（读取即清空历史）/ producer-passthrough-zero（生产者暴露恒0）各1个原Assert红并精确恢复。

**最终全量/提交。** final-v1于13:59:07–14:03:30 +08:00完成四次Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6965 PASS / 0 FAIL / 0 SKIP：Core197 / Security66 / Protocol303 / Integration3 / Transport5229 / Acceptance571 / Capture296 / Sessions300。旧6961项完整四字段身份保留，新增4项，删除0；本片未修改任何旧测试，无同步修正桥。summary 10538字节 / SHA `e0387f13e8debf531111dd8d6ebddb86ecf50a0b2dd72dc4438685ae86e6b985`。代码`dcd5eea58fb6cde4390f1dc3c0735f5ad4f70db8`已提交，3files / +205 / -0；当日普通push，`git ls-remote origin refs/heads/main` 实读完整号与本地 `git rev-parse` 逐字相同，远端一致已核对（此处不写 HEAD hash，避免自引用过期）。

**独立审计与打包。** audit-v1于14:03:55–14:04:00 +08:00原进程46908 / 外层 / 主线程exit0；report.json 994301字节 / SHA `ecd067530ce4d7a04415e169e022ae5b0c431d3e78ce02608d5fdb5b2152cc65`；434源 / 180观察原件 / 40TRX / 27869唯一execution，旧6961完整身份 +4 / -0，前片三份成品（画质接线ZIP/TXT/SHA）摘要保持。打包190条目（ZIP/TXT/SHA三成品及全条目SHA/CRC见 `outputs/M6-实际帧率指标-SHA256-20261023.txt` 与 `outputs/m6-actual-fps-20261023/package-verification.json`，全条目SHA/CRC及包外四份原件回读通过）。

**限制与下一步。** 本片只有指标：产品侧尚无读取者，auto quality控制器未接，画质改档来源仍只有主机档位；实际帧率是本机受控夹具（合成采集+桩编码器）下的编码计数与Stopwatch比值，不等于GDI实采、显示帧率、网络发送帧率或双机/真人/实屏验收。下一步接scale与quality presets、ping RTT、让auto quality控制器消费本指标，再真实Host/Client产品联调与M6真机双端验收；当前无需人工前置。无M6产品包；不重验M4/M5、不启M7、不改网络/防护/权限/Undo/旧ZIP。

### 19.70 M6 命名画质预设（2026-10-23）

**起点/范围（ADR114）。** dcd5eea/6965继续。M6任务清单里的 `scale` 已经真正落地（`WpfJpegFrameEncoder` 按 `settings.Scale` 缩放、JPEG 质量直接透传），`actual FPS` 上一片已完成；剩下 `quality presets` 只有 `VideoQualitySettings` 上三个零散的静态属性（Balanced/LowLatency/HighQuality），没有「按名字选档」的入口，界面与未来的自动画质控制器都无法用预设名工作。本片只补这一层：新增 App 层 internal 预设表并接到按会话画质解析器；不改 Core、不改 `Clamp` 的宽松钳制语义、不改协议、不改 App/Host 默认行为。

**预设表。** `VideoQualityPresets` 把预设名与合法档位一一对应（low-latency / balanced / high-quality），档位数值以 `VideoQualitySettings` 三个静态预设为**唯一事实来源**，本表不重复定义数值。解析严格：未知名、空、空白、大小写不符、前后带空白一律拒绝，**绝不静默回落到其他预设**——这与 `Clamp` 的宽松钳制是两套语义，后者保持原行为不变。名称按 `StringComparison.Ordinal` 精确匹配。`Resolve` 返回前仍走 `FramePipeline.ValidateSettings`，保证预设表本身始终落在 5/10/15/20/30 FPS、50/67/75/100% 缩放、JPEG 40..85 且非 Auto。`NameOf` 反查预设名，不属于任何预设的手工档位返回 null。

**接线。** `SessionQualityResolver` 新增 `SetPreset(Guid, string)`，内部复用既有 `Set`（含空会话标识拒绝与严格校验），未知名同样拒绝。这样「按预设名为某个会话改档」成为可用入口，而不需要调用方自己拼 fps/scale/jpeg。本片没有让 Host 暴露新的公开入口、也没有加界面选择——真实改档来源要等 RTT 与帧率都就位后的 auto quality 控制器。

**测试与变异。** 新增19项：名称稳定有序且唯一、每个名字对应文档化的档位、7个未知名/不精确输入全部拒绝、null 单独抛 ArgumentNullException、TryResolve 的成功与失败两路、NameOf 命中与「非预设返回 null」、每个预设都落在严格允许范围内、SetPreset 只影响目标会话、未知名拒绝且保留原值、空会话标识拒绝且不落表。首轮及两次恢复定向各19 PASS / 0 FAIL / 0 SKIP。五正式变异 preset-name-mismatch（high-quality 错配 Balanced）/ resolve-fallback-unknown（未知名回落）/ try-resolve-always-true（未知名也报成功）/ nameof-never-null（反查永不返回 null）/ set-preset-fallback（SetPreset 未知名回落）分别1 / 7 / 1 / 3 / 1个原Assert红并精确恢复。

**首轮抖动处理（显式记录）。** 首轮 final-v1 出现既有 `HostDisplayRequestReaderTests.StopBeforeOriginalContinuationExitNotifiesButNeverDispatches(call: 1)` 在整仓并行负载下抖动失败（§19.66 已记录过同一条）。单独复跑该用例 2 项全绿，且本片工作树只含 App 两个文件与一个新增 Acceptance 测试类、未触及 Transport 任何代码；该轮源码与被采纳的 final 同源，审计 history 形态仅支持构建门禁失败，故不保留为历史 stage，连同收据删除后整体重跑。

**最终全量/提交。** final-v1于17:04:38–17:08:53 +08:00完成四次Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各6984 PASS / 0 FAIL / 0 SKIP：Core197 / Security66 / Protocol303 / Integration3 / Transport5229 / Acceptance590 / Capture296 / Sessions300。旧6965项完整四字段身份保留，新增19项，删除0；本片未修改任何旧测试，无同步修正桥。summary 10643字节 / SHA `7daa100b2e6bbc0293d1115bb8fa8c61b8e5182fda94797ad212fd0819d48a59`。代码`ebd5e3d71f7104f73e6b47060cb38b90a9099052`已提交，3files / +219 / -0；当日普通push并 `git ls-remote` 实读完整号核对一致（此处不写 HEAD hash）。

**独立审计与打包。** audit-v1于17:09:18–17:09:23 +08:00原进程20792 / 外层 / 主线程exit0；report.json 1025903字节 / SHA `ffc87cd91faf3211fa2821bbe9fcdfc79df62960765cd1293f8573ee7abf2497`；436源 / 180观察原件 / 40TRX / 27968唯一execution，旧6965完整身份 +19 / -0，前片三份成品（实际帧率指标ZIP/TXT/SHA）摘要保持。打包成品见 `outputs/M6-画质预设-SHA256-20261023.txt` 与 `outputs/m6-quality-presets-20261023/package-verification.json`，全条目SHA/CRC及包外四份原件回读通过。

**限制与下一步。** 本片只是「预设名→档位」的解析层加按会话设档入口：界面还没有预设选择，Host 也没有对外暴露按预设改档的入口，自动画质控制器未接，产品当前仍表现为主机档位。定向只覆盖 Acceptance.Tests（新增测试所在项目），其余项目既有测试由双配置全量覆盖；五项变异均为单生产源单点替换，不声称穷举预设解析的全部输入形态。下一步接 ping RTT，再让 auto quality 简单控制器消费 RTT 与上一片的实际帧率、通过本片预设名执行降档，然后真实 Host/Client 产品联调与 M6 真机双端验收；当前无需人工前置。无 M6 产品包；不重验 M4/M5、不启 M7、不改网络/防护/权限/Undo/旧ZIP。

### 19.71 M6 ping/pong 与往返时延计量（2026-10-24）

**起点/范围（ADR115）。** ebd5e3d/6984继续。M6任务清单的 `ping RTT` 此前完全空白：`src` 里检索不到任何 Ping/KeepAlive/RTT 代码，而规格 04 章第 11 节要求 Control 消息类型至少含 `ping` 与 `pong`。本片只补帧合同与计量内核：新增三个生产文件（PingFrame / PongFrame / PingRttMeter）与两个 Transport 测试类；**不接定时 ping loop、不收发字节、不建立连接、不改变既有认证或视频路径**。

**帧合同。** 两个帧沿用既有 Control 帧的严格形态：4 KiB 字节预算、固定字段集（`type`/`protocol`/`sessionId`/`seq`/`timestampMs`，pong 另加 `echoedSeq`）、重复字段拒绝、非整数与负数拒绝、非法 UTF-8 与残缺 JSON 拒绝、`sessionId` 必须等于预期会话。ping 与 pong 类型互不相认（错配按 payload 拒绝）。**pong 自己的 seq 与 echoedSeq 是两回事**——前者是本消息序号，后者才用于结算往返时延；这一区分由专用变异守护。

**计量内核。** `PingRttMeter` 用可注入的 `TimeProvider` 记录 ping 起点、按回显序号结算。四条硬约束：①待确认样本**有界**（`MaxPending = 32`），超出上限拒绝登记，绝不无界增长；②重复或未知回显不结算、也不改写最近值；③回显序号属于**对端数据**，负数或异常值只返回 false，**不让本进程抛异常**；④`Prune` 可清理超期未确认样本，对端不回 pong 时不堆积。`LatestRtt` 无样本时为 null，不虚构 0。ping 的发送节奏由上层会话决定，本类不定时。

**测试与变异。** 新增23项：ping/pong 序列化与严格解析往返、echo 回显、两类互不相认、空/超预算载荷、空会话上下文、会话不匹配、缺 echoedSeq、重复字段、负数与小数、残缺 JSON、序列化远低于预算；计量侧为注入时钟下的精确 RTT、无样本报 null、未知与重复回显不二次结算、负数回显不抛、待确认有界、重复序号拒绝覆盖、按年龄清理、Reset 清空、最新样本生效、并发下有界、系统时钟非负样本。首轮及两次恢复定向各23 PASS / 0 FAIL / 0 SKIP。五正式变异 pong-echo-ignored（回显写成自身 seq）/ meter-settle-any（未知回显也结算）/ meter-duplicate-settle（结算后不移除因而可重复）/ meter-unbounded（去掉上限）/ meter-no-prune（清理不移除）各1个原Assert红并精确恢复。

**最终全量/提交。** final-v1于10:21:34–10:26:23 +08:00完成四次Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各7007 PASS / 0 FAIL / 0 SKIP：Core197 / Security66 / Protocol303 / Integration3 / Transport5252 / Acceptance590 / Capture296 / Sessions300。旧6984项完整四字段身份保留，新增23项，删除0；本片未修改任何旧测试，无同步修正桥。summary 10496字节 / SHA `99c6e0c4ba33588fc4190542927efde383a0157761e5a2372ea037e4eccdc746`。代码`0ffc111256a80edd9bbbdb350e41937e0eff94e1`已提交，5files / +714 / -0；当日普通push并 `git ls-remote` 实读完整号核对一致（此处不写 HEAD hash）。

**独立审计与打包。** audit-v1于10:26:38–10:26:43 +08:00原进程52524 / 外层 / 主线程exit0；report.json 1034539字节 / SHA `f0b228485e7567b7c7c036a0722ff886697c7d0c8e146a5c0cd5d4ed783c5a44`；441源 / 180观察原件 / 40TRX / 28056唯一execution，旧6984完整身份 +23 / -0，前片三份成品（画质预设ZIP/TXT/SHA）摘要保持。打包成品见 `outputs/M6-pingRTT-SHA256-20261024.txt` 与 `outputs/m6-ping-rtt-20261024/package-verification.json`，全条目SHA/CRC及包外四份原件回读通过。

**限制与下一步。** 本片只有合同与计量内核：**没有定时 ping loop、没有把帧接到任何会话读写路径**，因此协议上还没有真正发出过一个 ping；RTT 也尚未被任何画质逻辑消费。注入时钟下的 RTT 是确定性测量，不等于真实网络往返、网卡排队或 TLS 开销。定向只覆盖 Transport.Tests（新增测试所在项目），其余项目既有测试由双配置全量覆盖；五项变异均为单生产源单点替换，不声称穷举并发时序。下一步是 auto quality 简单控制器：消费本片 RTT 与 §19.69 的实际帧率，通过 §19.70 的预设名执行降档，然后真实 Host/Client 产品联调与 M6 真机双端验收；当前无需人工前置。无 M6 产品包；不重验 M4/M5、不启 M7、不改网络/防护/权限/Undo/旧ZIP。

### 19.72 M6 真实 Control ping/pong 与按会话 RTT（2026-10-28）

**起点/范围（ADR116）。** 0ffc111/7007继续。上一片只有帧合同与计量内核：ping/pong 的严格解析、echoedSeq 回显、可注入时钟 RTT、有界待确认都已验证，但协议上还没有真正发出过一个 ping。本片把它接到真实认证会话：Host 复用目录/回执的唯一 `HostControlWriter` 发 ping，Client 的唯一 `ControlDisconnectMonitor` 读 ping 并由无队列 pong 单槽回 `echoedSeq`，Host 的唯一 `HostDisplayRequestReader` 按回显序号结算，并按真实 sessionId 登记/注销遥测。**本片不做任何调档**：不改画质、不接控制器、不改协议字段、不动安全约束。

**Host 侧。** `HostControlWriter.TryWritePingAsync` 在既有写槽内发出 ping：目录或回执正在写时返回 null（跳过本轮、不排队、不消费序号）；只有真正被写槽接纳的序号才 `PingRttMeter.Arm`，因此不会出现“没发出却挂着 pending”的幽灵样本。`HostControlPingLoop` 是目录发出后才启动的周期 worker，使用注入/系统时钟的 `Task.Delay`，每轮先 `Prune` 超期样本再发下一枚；写失败会请求写者停止，其 worker 由 `HostControlWriterLifetime` 在停止路径里 join，不产生脱离会话的后台根。`HostDisplayRequestReader` 现在接受 `PongFrame`，只用 `EchoedSequence` 结算，未知/重复回显不占用切屏处理槽、也不令本进程抛异常。

**Client 侧。** 新增私有 `ClientControlPongState`：与切屏请求共用同一 Control 写槽，槽忙（正在发切屏请求或上一枚 pong）时本轮直接跳过，由 Host 的超期清理兜住；唯一切屏请求可等当前 pong 的原写入退出后再发，但原 attempt 仍按既有路径完整 join，不把“等通知”当作资源已释放。pong 的原 worker 进入 `CloseAndJoinAsync`、`LifetimeErrors` 与释放证据，停止时随父会话一起排空。

**按会话遥测。** `ControlPingTelemetry` 只按真实 sessionId 登记计量器，注销必须传入同一实例（旧连接收尾不会误删新连接），对外只暴露 `LatestRtt/PendingCount/SampleCount` 快照。产品 Host 通过 `ControlAuthContext.WithControlPing(telemetry, 2s, 5s)` 显式启用；不配置的旧调用方/夹具不会多发任何帧。

**测试与变异。** 新增3项（真实字节路径）：写者侧 ping 共享序号且目录占用时跳过不 Arm（起始序号 41→ping 42）、Host ping 收到真实 Client pong 并结算按会话 RTT、遥测注销必须同一实例且替换实例不被误删。集成用例用本机回环 TCP 裸流承载真实帧读写（不冒充 TLS 握手），Host ping 起始序号设为 41、Client pong 从 0 开始，避免“pong 误回自己的 seq”被序号巧合掩盖。initial / restored-v1 / restored-v2 三轮定向各 3 PASS / 0 FAIL / 0 SKIP；五正式变异 writer-ping-not-armed（写者不 Arm）/ reader-settles-own-sequence（结算 pong 自己的 seq）/ client-echoes-own-sequence（pong 回自己的 seq）/ client-never-pongs（不回 pong）/ telemetry-forgets-instance（注销不核对实例）各 1 个原 Assert 红并精确恢复。

**最终全量/提交。** final-v1 于 11:17:19–11:21:19 +08:00 完成四次 Rebuild 零警告零错误，六 dotnet 及 worker/launcher 完整 wait0；Debug/Release 各 7010 PASS / 0 FAIL / 0 SKIP（Integration3 / Core197 / Sessions300 / Security66 / Capture296 / Protocol303 / Acceptance590 / Transport5255）。旧 7007 项完整四字段身份保留，新增 3 项，删除 0；本片未修改任何旧测试，无同步修正桥。代码 `9fb249fef37e46768013fe66922fd74df3b097dd` 已提交，15files / +756 / -22（3 新增生产 + 11 生产改 + 1 新增 Transport 测试类）。

**独立审计与打包。** audit-v1 原进程 / 外层 / 主线程 exit0；report.json 1041813 字节 / SHA `43f88ae2ebd4e3e4a984b92bbcdcbb2d6c01b7dcb70df4110c19a7d7ecc9d436`；445 源 / 183 观察原件 / 40 TRX / 28048 唯一 execution，旧 7007 完整身份 +3 / -0，前片三份成品（pingRTT ZIP/TXT/SHA）摘要保持。打包成品见 `outputs/M6-Control真实pingRTT-SHA256-20261027.txt` 与本目录 `package-verification.json`：202条目，ZIP 8835904字节 / SHA `8542ff81d50e8b10f283b062749299c7f84c75a49e06cd588fc5f4b54f2cd353`，全条目SHA/CRC及包外四份原件回读通过；验收TXT 4176字节 / SHA `7d276bcf02f99a38f499663974df74744fd05f463c230ab8ce536a52eda7f054`。当日普通push并 `git ls-remote` 实读完整号核对一致（此处不写 HEAD hash）。

**两处环境与回归教训（显式记录）。** ①本机 NuGet 还原在当前会话不可用：`dotnet restore` 与 `dotnet nuget locals` 均报 `Value cannot be null (path1)`；还原资产本身完好，故 runner 的强制 Rebuild 改为带 `--no-restore`（仍完整 Rebuild 且要求 0 警告 0 错误），审计同步按旧/新 runner 区分期望 argv。②曾在 `BindCatalogLifetime` 里重复校验“启用 ping 必须 UseSessionWriter”，真实产品装配路径下会把连接直接关掉，`ProductHostDisplayCatalogTests` 三项立即失败（非抖动、非 ping 帧干扰，耗时仅 312ms）；`WithControlPing` 配置时已校验，这处重复校验删除后该测试类恢复 3/3。删除生产校验后已冻结源码改变，之前 initial/变异/恢复轮整体删除并重跑，未保留为历史 stage。

**限制与下一步。** 本片只有测量：RTT 尚未被任何画质逻辑消费，`SetPreset` 仍只影响之后创建的生产者，运行中调档必须走 `ReplaceableVideoFrameSource` 换代。集成测试用本机回环 TCP 裸流代替已认证 TLS 底座，只证明帧读写/结算/生命周期，不等于真实 TLS/双机/真人/实屏验收；五项变异均为单源单点替换，不声称穷举并发时序。下一步是 auto quality 简单控制器：消费本片 RTT 与 §19.69 实际帧率，按 §19.70 预设名通过显式画质准备创建候选并完成换代，随后真实 Host/Client 产品联调与 M6 真机双端验收。当前无需人工前置；无 M6 产品包；不重验 M4/M5、不启 M7、不改网络/防护/权限/Undo/旧 ZIP。

### 19.73 M6 自动画质遥测接缝与决策核心（2026-10-28）

**起点/范围（ADR117）。** 9fb249f/7010继续。上一片把 RTT 真正接到了会话，但自动画质还缺两件事：运行中换代必须知道“当前 active 生产者”和“当前显示器”，否则换代会悄悄回到主屏；决策也不能凭单帧抖动乱跳。本片只补这两件**输入与决策**：不改运行中管线、不创建/换代生产者、不接周期循环、不改协议与安全约束。

**遥测接缝。`FramePipelineVideoProducer` 增加 `Display`，由工厂把本次实际 `selected` 显示器随生产者交付；`ReplaceableVideoFrameSource` 增加 `ActiveProducer` 与 `HasReplacementInFlight`。`ActiveProducer` 只返回已发布的 active 代际，换代空窗为 null，绝不把尚未发布的候选或“最近创建的对象”冒充当前代；`HasReplacementInFlight` 让后续控制器不在换代未退出时再提交一次。两处都是 internal 只读快照，不新增公开 API。

**决策核心。** App 新增 `AutoQualityController`：按会话累积 RTT 与实际帧率样本，连续 3 个同向样本才在 `low-latency / balanced / high-quality` 之间升降一档，切换后立即清零；滞回区间（RTT 在 40–120ms 且帧率不构成升档）的混合样本清零计数。坏样本判据是 `RTT ≥ 120ms` 或 `实际帧率 < 当前档位目标 × 0.80`；好样本判据是 `RTT ≤ 40ms` 且 `实际帧率 ≥ 目标 × 0.95`。无 RTT、帧率为 0/非有限、未知名或手工档位一律不改档；未知/手工档位还会清空该会话计数。本类不定时、不收发字节、不接触会话表。

**测试与变异。** 新增14项（Acceptance 10 项决策 + 2 项当前代际接缝 + 2 项工厂目标显示器）：当前代际只认已发布 active、换代在途可见且第二换代被拒、显式目标与主屏默认都随生产者交付、目标元数据不符在创建依赖前拒绝；决策侧覆盖升降档逐级到边界、混合滞回清零、无样本不改档、未知/手工档位清空计数、按会话独立与 Reset/Clear。initial / restored-v1 / restored-v2 三轮定向各 14 PASS / 0 FAIL / 0 SKIP；五正式变异 controller-single-sample-switch（一次坏样本就换档）/ controller-bad-becomes-upgrade（坏样本反而升档）/ controller-good-becomes-downgrade（好样本反而降档）/ controller-zero-fps-counts（未启动帧率也算坏样本）/ controller-keeps-unknown-state（手工档位不清空计数）分别 2 / 2 / 2 / 1 / 1 个原 Assert 红并精确恢复。

**最终全量/提交。** final-v1 于 12:15:43–12:19:42 +08:00 完成四次 Rebuild 零警告零错误，六 dotnet 及 worker/launcher 完整 wait0；Debug/Release 各 7024 PASS / 0 FAIL / 0 SKIP（Integration3 / Core197 / Sessions300 / Security66 / Capture296 / Protocol303 / Acceptance604 / Transport5255）。旧 7010 项完整四字段身份保留，新增 14 项，删除 0；未修改任何旧测试，无同步修正桥。代码 `4fd40993bc798c4a9e1be8ea4130ed187295d50c` 已提交，9files / +450 / -1（1 新增生产 + 3 生产改 + 3 新增 Acceptance 测试类；其中 Sessions.csproj 新增 Acceptance.Tests 友元、Acceptance.Tests.csproj 新增 Sessions/Transport 测试引用，均为测试接缝，不改公开 API）。

**独立审计与打包。** audit-v1 原进程 / 外层 / 主线程 exit0；report.json 1046041 字节 / SHA `d30670d174b64035b422b4b91a40949ee09419604f18add566f9ebd70761d3d3`；449 源 / 179 观察原件 / 40 TRX / 28118 唯一 execution，旧 7010 完整身份 +14 / -0，前片三份成品（Control 真实 pingRTT ZIP/TXT/SHA）摘要保持。打包成品见 `outputs/M6-自动画质决策与遥测接缝-SHA256-20261028.txt` 与本目录 `package-verification.json`：196条目，ZIP 8814962字节 / SHA `d486823eff1a5b7a56b175361e2562761b5411b24b0bc00b8877e410358237dc`，全条目SHA/CRC及包外四份原件回读通过；验收TXT 4093字节 / SHA `f4e281d9384f091f3d82beb1eae57e002fd6d1db5c54f358c1560d8a1da28475`。当日普通push并 `git ls-remote` 实读完整号核对一致（此处不写 HEAD hash）。

**本轮过程教训（显式记录）。** ①新增测试跨三个项目时，证据链的正式定向要求“定向 Counter 覆盖全部新增身份”，单项目定向不成立；本片最终把三个新增测试类都放在 Acceptance.Tests（为其开放 Sessions/Transport internal 友元与测试引用），保持单程序集定向，没有把 runner 改成解决方案级定向或放宽审计。②变异替换串必须唯一：`names[index + 1]` / `names[index - 1]` 在升降档两支各出现一次，第一版替换因不唯一被逆替换校验拦下，改为带上下文与注释标记后重跑，缺陷产物已删除。

**限制与下一步。** 本片仍不是产品可见变化：没有周期循环、没有把决策接到真实会话，画质调控尚未生效；`SetPreset` 仍只影响之后创建的生产者，运行中调档必须走 `ReplaceableVideoFrameSource` 换代。ActiveProducer 只证明“当前代际对象是谁”，不证明发送、显示或采集成功；实际帧率是启动以来累计平均编码帧率，不是目标帧率或显示帧率。下一步：HostRuntimeService 周期换代接线，用本片 active 生产者与 Display、按会话 RTT（§19.72）和决策目标，经 `CreateForDisplayWithSessionQualityAsync` 创建候选并 `ReplaceVideoProducerAsync` 换代，成功后才 `SetPreset`；随后真实 Host/Client 产品联调与 M6 真机双端验收。当前无需人工前置；无 M6 产品包；不重验 M4/M5、不启 M7、不改网络/防护/权限/Undo/旧 ZIP。
