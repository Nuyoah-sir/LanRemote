# LanRemote

局域网屏幕共享与远程控制工具。仅供自用，**无账号、无云服务器、无公网穿透**。
默认只允许同一 IPv4 子网的设备发现与连接，且连接必须通过访问密钥挑战认证。

完整施工规格位于 `LanRemote_Implementation_Package/`，当前进度见根目录的 `HANDOFF.md`。

## 当前状态

**M1 — 设备身份与安全存储（已完成）**，此前 **M0 — 仓库骨架（已完成）**。

- M0：solution、8 个 src 项目、4 个测试项目、WPF 主窗口、DI/日志/配置、单实例 Mutex
- M1：稳定 deviceGuid、可派生设备码 `QPKE-2CPC` 这类形态、128-bit 访问密钥、
  DPAPI 保护的 `secrets.bin`、自签名 ECDSA P-256 设备证书与 SHA-256 指纹、
  主窗口的密钥「显示 / 复制 / 重新生成」

下一步是 **M2 — 网卡筛选 + UDP 发现**。

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
