# LanRemote

局域网屏幕共享与远程控制工具。仅供自用，**无账号、无云服务器、无公网穿透**。
默认只允许同一 IPv4 子网的设备发现与连接，且连接必须通过访问密钥挑战认证。

完整施工规格位于 `LanRemote_Implementation_Package/`，当前进度见根目录的 `HANDOFF.md`。

**两机手工验收尚未执行（NOT RUN）。验收手册见 [`docs/TWO_MACHINE_ACCEPTANCE.md`](docs/TWO_MACHINE_ACCEPTANCE.md)，
便携版验收包由 `scripts/acceptance/make-package.py` 生成。验收未通过前不进入 M3。**

## 当前状态

**版本 `0.1.0-m2`**。已完成：**M0 → M1 → M1.1（安全审计）→ M1.2 → M1.3 → M2（局域网发现）→ M2.1（发现收口修复）**。
基线提交 `664e558`；Last code commit 见 `HANDOFF.md` 第 1 节。当前阶段测试 **408 passed / 0 failed**。

**M1 已封板；M2 / M2.1 code 完成，两机手工 DoD 仍未执行（NOT RUN，见 HANDOFF 第 9 节）。
两机验收完成前不进入 M3。**

- M0：solution、8 个 src 项目、4 个测试项目、WPF 主窗口、DI/日志/配置、单实例 Mutex
- M1：稳定 deviceGuid、可派生设备码 `QPKE-2CPC` 这类形态、128-bit 访问密钥、
  DPAPI 保护的 `secrets.bin`、自签名 ECDSA P-256 设备证书与 SHA-256 指纹、
  主窗口的密钥「显示 / 复制 / 重新生成」
- M1.1：证书私钥导入改为 `EphemeralKeySet`（ADR-018，取代 ADR-016）、
  `UpdateAsync` copy-on-write 事务语义（ADR-019）、证书状态 fail closed 与
  `secrets.bin` 严格校验（ADR-020）、秘密 `byte[]` 生命周期清零
- M1.2：`ReadAsync` 只发副本、已有证书时启动零写入、`NewPfxPassword` 清零、
  `TryDecodeExact` 失败不返回部分解码字节、partial 证书恢复测试加强
- M1.3：ECDSA 证书 KeyUsage 改为 `digitalSignature` only（RFC 5480，`keyEncipherment`
  不属于 EC profile）；serverAuth EKU 保留

- M2：`NetworkInterfaceSelector`（RFC1918 + 物理类型 + 虚拟网卡过滤）、`SubnetPolicy`、
  UDP 组播 `239.255.77.77:45872` TTL=1 + directed broadcast probe、announce/probe/unicast 回应、
  有界设备缓存（256）与有界更新队列（512 DropOldest）、7 秒 TTL、MainWindow 真实设备列表
- M2.1：probe unicast 回应改为固定打到 `remote.Address:45872`（原 bug 会打到对方随机临时端口）、
  每个 sender 显式设置 `IP_MULTICAST_IF` 出口网卡、capabilities 校验收紧
  （raw 条数先判上限 / 空白与 null 拒绝 / 控制字符在 Trim 前拒绝）、
  discovery 启动失败不再被配置加载文案覆盖、`StartAsync` 失败路径 Dispose linked CTS

> 注意：本机开发环境唯一的活跃网卡是 `172.100.166.220`（公网段，**不属于 RFC1918**），
> 因此在这台机器上发现功能会输出「没有找到任何合格的私有 IPv4 网卡」——这是预期行为，不是 bug。
> 需要 192.168.x.x / 10.x.x.x 的网络才能验证发现。

### 设备证书 profile

ECDSA P-256 / `id-ecPublicKey` / SHA-256 自签名、5 年有效、`CA=false`、
`KeyUsage = digitalSignature`（critical）、EKU 含 `serverAuth`、私钥 `EphemeralKeySet` 载入。

下一步是 **M3 — TLS Host/Client + 同子网连接校验**（等两机手工验收结果，未验收不开工）。

## 本机构建环境

.NET SDK 装在**用户级目录**（不在 `C:\Program Files\dotnet`），因此在没有把它加入 PATH 之前，
直接用裸 `dotnet` 命令会报 `command not found`。

先加载环境，再执行 dotnet 命令：

```bash
# Git Bash
source scripts/env.sh
dotnet build LanRemote.sln
dotnet test LanRemote.sln
```

```powershell
# PowerShell
. .\scripts\env.ps1
dotnet build LanRemote.sln
dotnet test LanRemote.sln
```

也可以一次性生效：

```bash
export PATH="/c/Users/Administrator/.dotnet:$PATH"
```

> 若希望永久生效，需要把 `C:\Users\Administrator\.dotnet` 加入 PATH 用户环境变量，
> 并把 `DOTNET_ROOT` 设为同一路径。当前并未修改你的环境变量。

## 已安装 SDK

| 项 | 版本 |
|---|---|
| SDK | 10.0.401 |
| Microsoft.NETCore.App | 10.0.12 |
| Microsoft.WindowsDesktop.App（WPF） | 10.0.12 |

## 项目结构

```text
src/
  LanRemote.App          WPF UI、窗口、ViewModel、DI 启动（net10.0-windows）
  LanRemote.Core         领域模型、配置、公共抽象（net10.0）
  LanRemote.Discovery    UDP 发现、网卡筛选、设备缓存
  LanRemote.Transport    TLS/TCP、帧协议、连接状态机
  LanRemote.Security     DPAPI、证书、访问密钥、认证挑战（net10.0-windows）
  LanRemote.Capture      屏幕采集、缩放、JPEG 编码（net10.0-windows）
  LanRemote.Input        SendInput、坐标映射、权限闸门（net10.0-windows）
  LanRemote.Sessions     Host/Client 会话编排
tests/
  LanRemote.Core.Tests          单位测试
  LanRemote.Security.Tests      安全不变量（Windows TFM）
  LanRemote.Protocol.Tests      协议常量护栏
  LanRemote.IntegrationTests    文件系统集成测试
docs/   规格副本、DECISIONS、协议说明
scripts/ 环境脚本（防火墙脚本在 M10 补充）
```

## 数据目录

```text
%LOCALAPPDATA%\LanRemote\config.json     普通设置
%LOCALAPPDATA%\LanRemote\secrets.bin     DPAPI 保护的秘密（M1 起写入）
%LOCALAPPDATA%\LanRemote\logs\           日志
```

秘密文件与配置文件严格分离：`AppConfigStore` 永远不会触碰 `secrets.bin`，
`DpapiSecretVault` 也永远不会写 `config.json`，这两条都有测试守护。

### secrets.bin 的内容与保护方式

```text
[0..3]   magic  = "LRSC"
[4]      version = 1
[5..8]   payload 长度（大端 uint32）
[9..]    payload = DPAPI(UTF-8 JSON)，JSON 内含：
                   deviceGuid / accessKey(Base32) / certificatePfx / certificatePfxPassword
```

- 加密：`ProtectedData.Protect(..., CurrentUser)`，换用户或换机器都解不开
- 写入：临时文件 + 原子替换，不会留下写了一半的文件
- 文件损坏时**不静默重建**（否则「损坏」会被伪装成「首次运行」），而是抛 `InvalidDataException`
- 长度必须**严格等于**声明长度；payload 内的 bundle 版本也必须与实现一致（ADR-020）
- 证书字段（`certificatePfx` / `certificatePfxPassword`）必须同时存在或同时缺失，
  缺一半时**拒绝**而不是重新签发证书——否则证书指纹会静默改变（ADR-020）
- 私钥导入使用 `EphemeralKeySet`：运行时只在内存，磁盘上的副本只有这份 DPAPI 文件（ADR-018）
