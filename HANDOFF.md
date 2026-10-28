# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-10-28（最新完成片§19.76/ADR120：自动画质显式开关已验证，代码77aca97，9files/+403/-15）。同日先完成§19.75/ADR119 产品装配层自动画质与ping/RTT联调 d42d65b/7031（1file/+364/-0，仅新增测试）；同日先完成§19.74/ADR118 Host自动画质周期换代接线 be022d5/7028（8files/+447/-5）；同日先完成§19.73/ADR117 自动画质遥测接缝与决策核心 4fd4099/7024、§19.72/ADR116 真实Control ping/pong与按会话RTT 9fb249f/7010；此前完成§19.71/ADR115 ping/pong帧与往返时延计量内核 0ffc111/7007、§19.70/ADR114 命名画质预设 ebd5e3d/6984与§19.69/ADR113 实际帧率指标 dcd5eea/6965。此前回填：§19.66/ADR110 产品侧切屏接线 93d72e2/6940、§19.67/ADR111 每会话画质底座 32420e4/6951、§19.68/ADR112 每会话画质接线 e250ca2/6961，四片均已push并核remote完整号一致；再前 §19.65/ADR109 内部显式多屏GDI采集 1028888/6922。新增53（45合成native+8真实offscreen），三轮定向各231PASS，四正式变异3/21/1/1原Assert红恢复；final-v2四Rebuild零警告错误、双配置各6922PASS，独立audit-v2退出0。旧Transport测试仅补54字节派发等待，原断言/身份保留，Capture生产和测试冻结。默认App仍旧主屏路径，offscreen非真实副屏/混合DPI/热插拔或双机验收；下一片接App Capture/Sessions友元、Host每会话prepare/writer、Client启用及窗口请求。冻结settings，不重验M4/M5、不启M7、不改网/防护/权限/旧ZIP。）**
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

- **当前里程碑：M5按2026-10-12用户稳定反馈收口；M6进行中。** 最新完成片§19.76 自动画质显式开关（ADR120），代码77aca97已普通push并核remote一致，9files/+403/-15（3生产改+3 Sessions 遥测改+1新增Acceptance开关测试类+2旧测试受控同步/桩补齐）；新增3项，initial/两次恢复定向各3PASS，五正式变异switch-always-on / switch-always-off / switch-start-preset-not-balanced / switch-quality-not-applied / switch-enabled-kept-on-stop各1个原Assert红并精确恢复。final-v1于15:27:37–15:31:36 +08:00四Rebuild零警告零错误，六dotnet及worker/launcher完整wait0；Debug/Release各7034 PASS / 0 FAIL / 0 SKIP（Integration3/Core197/Sessions300/Security66/Capture296/Protocol303/Acceptance614/Transport5255）；独立audit-v1原进程 / 外层 / 主线程exit0，454源 / 180观察原件 / 40TRX / 28144唯一execution，旧7031完整身份 +3 / -0。关闭时无周期worker、会话无受管起点（与引入开关前逐字一致）；开启时会话以命名预设balanced为起点并启动周期worker，显式主机档位优先于回退起点，停机join并复位开关。**为何要有这一片**：§19.75 用探针证实默认配置（未勾手动画质/预览帧率）下 `_sessionQuality.Resolve(sessionId)` 为 null，`HostAutoQualityLoop` 首道闸直接跳过，自适应一次都不会动；按用户决定改为显式开关，而不是静默提高默认采集档位。**仍未证明换代已在产品会话生效，也未做两机/真人/实屏验收。** 前一片§19.75 产品装配层自动画质与ping/RTT联调（ADR119）d42d65b/7031；再前§19.74 周期换代接线 be022d5/7028、§19.73 遥测接缝与决策核心 4fd4099/7024、§19.72 真实ping/pong与RTT 9fb249f/7010。


---

## 19.76 自动画质显式开关（ADR-120，代码 `77aca97`，7031→7034）

**起因（有实跑证据，不是推断）**：§19.75 收尾时用一次性探针测得——默认配置下
`_sessionQuality.Resolve(sessionId)` 返回 **null**（探针输出 `resolved=null`），
`HostAutoQualityLoop` 的 `if (_quality.Resolve(sessionId) is not { } current) return;` 直接跳过。
链路是：`MainViewModel` 仅在勾选手动画质/预览帧率档时才传 `previewSettings` → null；
`SessionQualityResolver` 无主机当前档位就不落表；周期 worker 无当前档位不改档（ADR-117 的刻意决定）。
**结论：不勾选画质档位时，M6 自适应一次都不会动。** 用户拍板：加显式开关，默认关闭。

**改动**
- `HostRuntimeService.StartAsync(..., bool autoQuality = false)`：开关关闭时不建 worker、
  会话起点回退仍为 null（逐字等于引入开关前）；开启时会话起点回退 `balanced`，并建且启动周期 worker。
  停机 join worker 后复位开关。
- `MainViewModel` + `MainWindow.xaml`：新增「本次自动画质」勾选（本次共享生效，与既有预览档一致，
  不写配置），经 `EnableViewOnlySharingAsync(autoQuality:)` 传入。
- Sessions 遥测接缝：`FramePipeline.Settings` / `FramePipelineVideoProducer.Quality` / `IVideoProducerTelemetry.Quality`，
  使产品测试能断言**本代际实际采用的档位**，而不是只查解析器字典。

**三项断言（真实产品 Host + 真实回环 TLS + 真实认证与视频附着）**
1. 关闭：无 worker、会话无受管起点、active 代际档位不是任何命名预设。
2. 开启：worker 已启动；会话起点与实际代际档位均为 `balanced`；停机 join worker、清空并复位开关。
3. 开启且主机显式指定档位：显式档位优先，起点不被回退覆盖。

**验证**：initial / restored-v1 / restored-v2 各 3 PASS；五变异各 1 个原 Assert 红并精确恢复。
final-v1 15:27:37–15:31:36 +08:00 双配置各 **7034 PASS / 0 FAIL / 0 SKIP**（Acceptance 611→614）。
audit-v1 exit 0，454 源 / 180 原件 / 40 TRX / 28144 execution，旧 7031 身份 +3 / −0；
旧测试仅一处受控同步（旧接线用例改为显式开启，两行文本块可逆、断言形式不变），逆替换校验通过。

**局限**：开关只解决「是否被管理」，不解决「是否真的换代生效」——换代需要连续同向样本与真实帧率，
本片未断言；真实会话仍是本机回环与真实本机审批窗口，不是两机或真人操作。

打包成品见 `outputs/M6-自动画质开关-SHA256-20261028.txt` 与本目录 `package-verification.json`。
