# LanRemote 项目长期记忆

## 项目定位

Windows 局域网屏幕共享与远程控制工具（自用）。**无账号、无云服务器、无公网穿透、无 UPnP/中继**。
默认只允许同一 IPv4 子网的 RFC1918 设备发现与连接，且必须通过访问密钥挑战认证。

规格合同位于 `LanRemote_Implementation_Package/`，**当前真实进度以仓库根目录 `HANDOFF.md` 为准**。

## 技术栈与约束（不可动摇）

- C# / .NET 10 LTS / WPF / x64；Nullable + ImplicitUsings
- TFM：`net10.0`（Core/Discovery/Transport/Sessions）与 `net10.0-windows`（App/Security/Capture/Input）
  —— **不要**改成 `net10.0-windows10.0.19041.0`，本机没装 Windows 10 SDK（ADR-010）
- UDP 发现端口 45872；TLS/TCP 45873；Control 与 Video 是**两条独立 TLS/TCP**（ADR-003，勿合并）
- 访问密钥必须 128-bit `RandomNumberGenerator`，禁止 `Random`、**禁止 6 位弱密码**
- 认证是 HMAC-SHA256 挑战，**明文 key 绝不上网**，且必须验证 serverProof——细节见规格第 9 节
- 视频管线：bounded queue（容量 1~2）+ DropOldest，**严禁无界队列**；实时性 > 完整性
- 输入注入必须过服务端权限闸门（session 已认证 ∧ permission==Control ∧ Host.AllowControl ∧ 本机审批 ∧ active）
- 以下安全机制任何时候都不得删除或弱化：同子网校验、RFC1918 私网限制、TLS、证书指纹 pinning、
  HMAC 访问密钥挑战、DPAPI 秘密存储、视频 attach token、ViewOnly/Control 权限隔离、本机审批、被控提示、紧急停止

## 密码学白名单

只允许：TLS、SHA-256、HMAC-SHA256、`RandomNumberGenerator`、
`CryptographicOperations.FixedTimeEquals`、Windows DPAPI、.NET 自带 X509/SslStream。
禁止自研算法、XOR 混淆、Base64 当加密、`Random()` 生成密码、MD5/SHA1 做认证。

## 出验收包（两机验收用）

```bash
source scripts/env.sh
dotnet publish src/LanRemote.App/LanRemote.App.csproj -c Release -r win-x64 \
  --self-contained true -o artifacts/m2.1-acceptance
python scripts/acceptance/make-package.py     # → LanRemote-<ver>-win-x64.zip
```
自包含包约 61 MiB / 444 条目，目标机无需装 runtime。手册 `docs/TWO_MACHINE_ACCEPTANCE.md`。
两个 `.ps1` 必须是 **UTF-8 with BOM**（PS 5.1 否则中文乱码），用 `scripts/acceptance/add-bom.py` 加。

## 常用命令

```bash
source scripts/env.sh           # 加载用户级 .NET SDK（必须，dotnet 不在 PATH）
dotnet build LanRemote.sln -c Debug
dotnet test LanRemote.sln -c Debug --no-build
```

## 用户协作偏好

- 中文；要结构化输出（表格、清单、API/字段说明）
- 结论先行，先给边界和证据，不接受「大概可能」
- 严禁伪代码、写死的假实现、伪造构建/测试结论（没跑过就写「未运行」）
- 改动最小化；每个里程碑必须 build + test + 更新 `HANDOFF.md` 后才能进入下一阶段

## 里程碑进度

M0 仓库骨架 —— **已完成**（build PASS / 87 tests PASS）
M1 设备身份与安全存储 —— **已完成**（build PASS / 175 tests PASS）
M1.1 Security Hardening —— **已完成**（build PASS / 189 tests PASS，git 基线 `664e558`）
M1.2 M1 Final Cleanup —— **已完成**（build PASS / 197 tests PASS，Last code commit `104f296`）
M1.3 ECDSA Certificate KeyUsage Fix —— **已完成**（build PASS / 200 tests PASS，Last code commit `9e75fce`）
M2 网卡筛选 + UDP 发现 —— **已完成**（build PASS / 382 tests PASS，Last code commit `857eaa6`）
M2.1 Discovery Final Fix —— **已完成，两机验收 PASS（20/20，2026-09-20）**
  build PASS / 408 tests PASS，Last code commit `313c542`
  验收物料与文档：`905fcc8` / `5e9f245` / `846794c` / `b8dbdb3`（无产品代码）
  ✅ 两机手工 DoD **PASS**（HANDOFF §9.1 有完整时间线 + 20 步逐条证据）
M3 TLS Host/Client + 同子网校验 —— 下一步（验收已 PASS，**等用户开工指令**）
  - 开工前先跑 **SslStream server/client 握手 spike**（本机实测）收口 ADR-018 的 EphemeralKeySet 风险
  - 外部模型只做「设计红队评审」（prompt 在 `docs/M3_EXTERNAL_REVIEW_PROMPT.md`）；
    **Windows/.NET 实测行为一律不问模型，本机测**；模型结论不得直接写进 HANDOFF
  - 本机实际系统：**Windows 11 专业版 25H2 / build 26200**（`Win32_OperatingSystem.Caption` 实测）
M3~M11 —— 未开始

## M1 关键存储事实（后续里程碑会依赖）

- `secrets.bin` = `LRSC`(4) + version(1) + length(4 BE) + DPAPI(JSON bundle)；
  bundle 内有 `deviceGuid` / `accessKey`(Base32 26 字符) / `certificatePfx` / `certificatePfxPassword`
- 访问密钥 = `RandomNumberGenerator.GetBytes(16)`；`Regenerate` 直接覆盖存储字段 → 旧 key 立即失效
- 设备证书 = 自签名 ECDSA P-256，5 年，含 serverAuth EKU，非 CA；
  **KeyUsage 只能是 digitalSignature**（RFC 5480，EC 证书不得声明 keyEncipherment，ADR-021）；
  指纹 = `SHA256(RawData)` 大写 hex，重载后稳定
- **禁止**为「把旧证书换成新 profile」加入静默自动重签逻辑（ADR-021 硬约束）
- 设备码 = `Base32(SHA256(deviceGuid) 前 5 字节)`，展示 `XXXX-XXXX`；**不是秘密**
- 证书加载用 `X509CertificateLoader.LoadPkcs12(pfx, pwd, EphemeralKeySet)`（ADR-018，取代 ADR-016）；
  **M3 必须补真实 SslStream 握手集成测试**才能确认这个策略够用，没测出来之前不许改回 PersistKeySet
- `DpapiSecretVault.UpdateAsync` 是 copy-on-write：先落盘成功才替换缓存，失败时磁盘与内存同时保持旧状态
- `ReadAsync` 只发 `Clone()`；证书已存在时加载走只读路径，**不重写 secrets.bin**
- `TryDecodeExact` 失败时 out 是 `Array.Empty<byte>()`（已 ZeroMemory），不会返回部分解码的秘密字节
- HANDOFF 记账用 `Last code commit` + `Working tree at validation`，**不写 HEAD hash**（避免自引用）

## M2.1 已锁死的两条发现不变量（别写回去）

- **probe unicast 回应目标 = `remote.Address : 45872`**，绝不是 `remote.Port`
  （probe 源端口是对方 sender 的随机临时端口，没人监听）
- **每个 sender 必须显式 `SetSocketOption(IP, MulticastInterface, 接口 IPv4 网络序 4 字节)`**；
  不设则多网卡时组播全走系统默认路由。实测：对地址做 `HostToNetworkOrder` 或传裸 index 都会抛
  `SocketException: 在其上下文中，该请求的地址无效`
- Discovery 对 `LanRemote.Protocol.Tests` 开了 `InternalsVisibleTo`，只服务上述两个 internal 纯函数

## 本机开发残留（非阻断、勿写进产品逻辑）

- `%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak` = M1.3 手工重置身份时留的旧开发备份
- 只存在本机，**不在源码包**（`git archive` 152 条目已验证不含 bak/secrets.bin/.workbuddy）
- 确认不再需要旧开发身份后**由施工环境手工删除**即可
- **硬约束：绝不把「自动删除身份备份」写进产品逻辑**（产品代码里不得出现 backups/.bak 相关清理）

## 验收用 lab 网络（两机验收现场实况）

- 用户的两台实机在 **`172.100.166.220` / `172.100.166.65`**（网关 .254，1000 Mbps 互通）
- **`172.100.x.x` 不是 RFC1918**（172 段只到 172.31）→ LanRemote 正确拒绝 → 设备列表空
- 处置：`scripts/acceptance/set-lab-ip.ps1 -Role A|B` 给两台各安排 `192.168.1.10` / `192.168.1.20`（/24）；
  同时 profile=Private + 放行入站 UDP 45872
- 教训：判定私有必须按数值区间，**不能用前缀字符串**（`172.100` 会被误判成私有）

## Windows IPv4 实测结论（血的教训，别再凭直觉）

**一张网卡只能 DHCP 或 静态，不能共存**（2026-09-20 实测，代价是用户断网两次）：

| 操作 | 预期 | 实测 |
| --- | --- | --- |
| `New-NetIPAddress` 在 DHCP 接口上追加地址 | 共存 | 接口 `Dhcp` → `Disabled`，租约**丢失** |
| `netsh interface ipv4 add address` | 共存 | 同上 |
| 追加后再删除该地址 | 复原 | 只剩 APIPA，无网关/DNS |

- 正确做法：整口切静态（用**当前同一套** IP/掩码/网关/DNS，不断网）→ 再追加第二地址（**不带网关**）
- 回滚：`netsh interface ipv4 set address source=dhcp` + `set dnsservers source=dhcp` + `ipconfig /renew`
- **`netsh` 退出码不可信**：已是 DHCP 时 `set address source=dhcp` 返回**非 0** 并输出
  「已在此接口上启用 DHCP。」，实为成功 → 必须用 `Get-NetIPInterface` 复核实际状态
- **PS 5.1 陷阱**：`($x | ForEach-Object { $_.IPAddress } -join ', ')` 会把 `-join` 当参数 → 抛异常；
  写 `$x.IPAddress -join ', '`（语法检查查不出来，只有真跑才暴露）
- `Set-NetConnectionProfile` 在刚切完静态时因网卡 `Identifying...` 会失败 → 需重试

## 本机网络环境（影响 discovery 验证）

- 以太网 = **172.100.166.220**，`Dhcp`，**不属于 RFC1918**（172.16/12 只覆盖 172.16–172.31）
- WLAN 现已连上，`10.65.156.134/24` `Dhcp` —— **是** RFC1918 私有地址
  （2026-09-20 复测更正：早期记录说 WLAN 媒体已断开，已过期）
- 「本地连接* 1/2」只有 APIPA `169.254.x.x`
- 所以本机**有**合格网卡；但电脑 B 不在 `10.65.156.x`，两机仍需 `192.168.1.0/24` lab 网段
- 改本机 IP 配置前先想清楚回滚路径 —— 已因此断网两次

## 产品形态决策（2026-09-20 用户拍板「三个都做，含改 IP 一键」）

**总原则**：终端用户永远不需要打开 PowerShell。要管理员权限就走 UAC（App 内提权），
不由用户手动跑脚本。但「改 IP 要无感」= **入口无感（在 App 内）+ 触发显式（用户自己点）**，
**绝不是静默自动执行**（本机已两次把自己搞断网）。

- **ADR-024 网络诊断进 UI**（M9，最晚 M10 发布前闭合）：discovery 失败原因不能再只写日志，
  UI 必须给「原因 + 网卡名 + 实际地址」；覆盖 05_UI_UX_SPEC §8 的未发现/防火墙阻止/网络断开
- **ADR-025 防火墙一键内置 App + UAC**（M10）：M10 原 `configure-firewall.ps1`/`remove-firewall.ps1`
  降级为可选离线入口；仍只放行 LocalSubnet；必须有按前缀精确撤销的契约；禁止后台静默建规则
- **ADR-026 临时私有地址一键**（不早于 M10）：用户显式选网卡 + 排除虚拟网卡 + 先整张切静态再追加
  + 撤销回 DHCP 并自校验；撤销幂等；只追加不删用户原地址；禁止静默自动改 IP
- 三条都已写进 `docs/DECISIONS.md` 与 `HANDOFF.md` §13/§14/§15/§16，**只定规则，未启动编码**
- ADR 工作副本 = `docs/DECISIONS.md`（010~026）；`LanRemote_Implementation_Package/10_DECISIONS.md`
  保持原始 9 条规格快照，不回写
- 已修编号缺陷：ADR-018（证书加载 EphemeralKeySet）曾被错标成 ADR-021（KeyUsage），勿再混淆

## 本机实测：.NET 10.0.12 的默认值（M3 直接依赖，别再猜）

- `JsonSerializerOptions.AllowDuplicateProperties` 默认 **True**，且**后者覆盖前者**——
  `{"type":"channel_hello","type":"video"}` 默认解析为 `video`。设 `false` 才抛 `JsonException`
- `MaxDepth` 属性值默认 **0**（= 采用内置上限 64），不是「默认 64」
- `UnmappedMemberHandling` 默认 `Skip`；尾逗号与注释默认已拒绝（抛 `JsonException`）
- `SslClientAuthenticationOptions`：`AllowTlsResume=**True**`、`AllowRenegotiation=**True**`
- `SslServerAuthenticationOptions`：`AllowTlsResume=**True**`、`AllowRenegotiation=False`
- `EnabledSslProtocols` 两端默认 `None`（= 交给系统默认），必须显式 `Tls12 | Tls13`
- 取证方法：临时 console 项目 + 反射打印 `SslXxxAuthenticationOptions` 全部属性默认值（放在 %TEMP%，不进仓库）
- **证书校验回调参数是 `X509Certificate`（基类）**，没有 `RawData` → pin 要用 `GetRawCertData()`

## 证书私钥载入 flag：ADR-016/018 都被证伪，正确解是 ADR-029

实测（真实 SslStream 服务端，3 flag × 3 协议 × 重复 3 次，本机 Win11 25H2 / .NET 10.0.12）：

- **`EphemeralKeySet` 9/9 失败**：`AuthenticationException: ... platform does not support ephemeral keys.`
  ← `Win32Exception 0x8009030E`（客户端只看到 `IOException: unexpected EOF`，要抓服务端异常）
- `PersistKeySet` 9/9 可用，但**磁盘留持久密钥副本**（CNG user keys 文件 dispose 后不删）
- **`X509KeyStorageFlags.Default`(0) 9/9 可用，且 dispose/GC 后删除临时容器** → M3 用这个
- **污染陷阱**：同进程先 `PersistKeySet` 导入过同一私钥后，`EphemeralKeySet` 会碰巧成功 →
  flag 结论必须**新进程 + 顺序受控 + 重复多次**，否则假阳性
- M3 必须同步改掉把旧 flag 锁成断言的 `ImportFlags_UsesEphemeralKeySetOnly`
