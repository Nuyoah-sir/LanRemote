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
M2.1 Discovery Final Fix —— **code 已完成**（build PASS / 408 tests PASS，Last code commit `313c542`）
  ⚠️ 两机手工 DoD **NOT RUN**（M2.1 修完仍是 NOT RUN，未验收不得进 M3）
M3 TLS Host/Client + 同子网校验 —— 下一步（等两机验收回填后才能开工）
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
- 处置：`scripts/acceptance/set-lab-ip.ps1 -Role A|B` 追加 `192.168.1.10` / `192.168.1.20`（/24）
  **共存地址**，不删原有地址（避免破坏用户现有上网）；同时 profile=Private + 放行 UDP 45872
- 教训：判定私有必须按数值区间，**不能用前缀字符串**（`172.100` 会被误判成私有）

## 本机网络环境（影响 discovery 验证）

- 唯一活跃网卡 IPv4 = **172.100.166.220**，**不属于 RFC1918**（172.16/12 只覆盖 172.16–172.31）
- WLAN 与「本地连接* 1/2」均为媒体已断开
- 因此本机跑 LanRemote discovery 必然输出「没有找到任何合格的私有 IPv4 网卡」——**预期行为**
- 要验证发现/连接，需要 192.168.x.x 或 10.x.x.x 的网络，或另开实验性开关（当前不做）
