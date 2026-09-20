# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-20 12:10 (+08:00)**

---

## 1. 当前状态

- **当前里程碑：M1 — 设备身份与安全存储，已完成（DoD 满足）**
- 上一里程碑：M0 — 仓库骨架（已完成，87 tests）
- 分支/commit：**无**（工作区尚未 `git init`）
- 总体状态：**里程碑完成**。按用户指令，M1 完成后停止，**不自动进入 M2**

## 2. M1 是否满足 DoD

`07_MILESTONES_AND_TASKS.md` 的 M1 DoD 为：
「重启应用 identity 不变；access key 不出现在日志/config」。

| DoD | 结论 | 证据 |
|---|---|---|
| 重启应用 identity 不变 | **满足** | 真实数据目录上两次启动：设备码 `QPKE-2CPC`、证书指纹前缀 `3A3D791A` 完全一致（见第 8 节）；并有 5 条自动化测试覆盖「模拟重启」 |
| access key 不出现在日志 | **满足** | 自动化：捕获日志断言不含 Base32 与 Hex 两种形态（`DpapiAccessSecretStoreTests.SecretNeverAppearsInLogs`）；手工：扫描真实日志文件，长度 ≥20 的 token 有 4 个，全部是文件路径/类名，**没有**任何由 Base32 字母表构成的串 |
| access key 不出现在 config.json | **满足** | `SecretStorageSeparationTests.ConfigJsonNeverContainsSecretMaterial` 断言连字段名都不出现；`Core.Tests` 里另有 M0 遗留的同向断言 |

M1 任务清单逐项对照：

| 要求 | 状态 |
|---|---|
| DeviceIdentityService | ✅ `LanRemote.Security.Identity.DeviceIdentityService` |
| 首次启动随机 deviceGuid | ✅ `SecretGenerator.NewDeviceGuid()`（在 vault 首次创建 bundle 时） |
| deviceGuid 持久保存、重启稳定 | ✅ 存 DPAPI bundle；跨重启测试 + 真实手工验证 |
| SHA-256(deviceGuid) 派生 DeviceCode | ✅ `LanRemote.Core.Identity.DeviceCode`，取哈希前 5 字节 |
| Crockford Base32 | ✅ `LanRemote.Core.Encoding.CrockfordBase32` |
| 展示格式 `7K3M-P9QX` | ✅ 8 字符按 4 分组 |
| `RandomNumberGenerator.GetBytes(16)` 生成 128-bit 密钥 | ✅ `SecretGenerator.NewAccessKeyBytes()` |
| Access Key Base32 编解码 | ✅ 同 CrockfordBase32；`SecretGenerator.TryDecodeAccessKey` 严格校验 16 字节 |
| 实现现有 `IAccessSecretStore` | ✅ `DpapiAccessSecretStore`（签名沿用 ADR-011 的异步形式，未改接口） |
| Windows DPAPI `ProtectedData` + CurrentUser | ✅ `DataProtection.Protect/Unprotect` |
| secrets 写入 `%LOCALAPPDATA%\LanRemote\secrets.bin` | ✅ |
| secret 不写入 config.json | ✅ 有测试守护 |
| secret 不进入日志 | ✅ 有测试守护 + 手工扫描 |
| ECDSA P-256 自签名设备证书 | ✅ `DeviceCertificateService`，含 serverAuth EKU、非 CA、5 年有效 |
| 证书私钥安全持久化 | ✅ PFX 存入 DPAPI bundle（ADR-015/016） |
| SHA-256 certificate fingerprint | ✅ 对 `RawData` 求值 |
| 重启后 fingerprint 稳定 | ✅ 测试 + 手工（两次均为 `3A3D791A` 前缀） |
| MainWindow 显示真实 DeviceCode | ✅ 绑定 `MainViewModel.DeviceCode`，另有「复制」按钮 |
| Access Key 默认遮挡 / 显示 / 复制 / 重新生成 | ✅ 全部在 `MainWindow` 与 `MainViewModel` 中 |
| 重新生成后旧 key 立即失效 | ✅ `RegenerateAsync` 直接覆盖存储字段；详见第 8 节说明 |

## 3. 已完成

- [x] Core：`CrockfordBase32`（编码/严格解码/分组）、`DeviceCode`（派生与校验）
- [x] Security：`SecretFile`（版本化二进制格式）、`DataProtection`（DPAPI + 指纹）、
      `SecretGenerator`（128-bit 密钥 / GUID / PFX 口令）、
      `DpapiSecretVault`（唯一读写入口、原子写、锁）、
      `DpapiAccessSecretStore`（实现 `IAccessSecretStore`）、
      `DeviceCertificateService`（ECDSA P-256、持久化、重载）、
      `DeviceIdentityService`（身份聚合）
- [x] App：DI 注册、MainViewModel 真实身份与密钥操作、MainWindow 的显示/复制/重新生成 + 剪贴板 30 秒自清
- [x] 测试：新增 88 条（Core 46 条新增、Security 42 条新增），**合计 175 条全部通过**
- [x] 文档：README 增加 secrets.bin 格式说明，`docs/DECISIONS.md` 新增 ADR-014~017

## 4. 尚未完成

- [ ] `git init` 与首次提交（工作区仍不是 git 仓库）
- [ ] **日志轮转**（M9，ADR-013 已记录）
- [ ] 防火墙脚本（M10）
- [ ] M2 及之后的全部里程碑

## 5. 本次修改文件

**新增**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Core/Encoding/CrockfordBase32.cs` | Crockford Base32 编解码、分组；拒绝非规范尾部位 |
| `src/LanRemote.Core/Identity/DeviceCode.cs` | `Base32(SHA256(deviceGuid)[..5])` → `XXXX-XXXX` |
| `src/LanRemote.Security/Secrets/SecretFile.cs` | `secrets.bin` 二进制格式（`LRSC` + version + length + DPAPI payload） |
| `src/LanRemote.Security/Secrets/DataProtection.cs` | DPAPI 封装 + 证书指纹计算 |
| `src/LanRemote.Security/Secrets/SecretGenerator.cs` | 128-bit 密钥、deviceGuid、PFX 口令 |
| `src/LanRemote.Security/Secrets/DpapiSecretVault.cs` | 唯一读写入口、原子写、并发锁 |
| `src/LanRemote.Security/Secrets/DpapiAccessSecretStore.cs` | 实现 `IAccessSecretStore` |
| `src/LanRemote.Security/Certificates/DeviceCertificateService.cs` | 自签名 ECDSA P-256 + 持久化 + 重载 |
| `src/LanRemote.Security/Identity/DeviceIdentityService.cs` | 身份聚合 |
| `tests/LanRemote.Core.Tests/CrockfordBase32Tests.cs` | 编解码测试 |
| `tests/LanRemote.Core.Tests/DeviceCodeTests.cs` | 设备码稳定性/唯一性/格式 |
| `tests/LanRemote.Security.Tests/TestInfrastructure.cs` | 临时根目录 + 捕获日志器 |
| `tests/LanRemote.Security.Tests/AccessKeyGenerationTests.cs` | 密钥随机性与格式 |
| `tests/LanRemote.Security.Tests/DpapiAccessSecretStoreTests.cs` | 存储往返、轮换、无明文、不出日志 |
| `tests/LanRemote.Security.Tests/SecretStorageSeparationTests.cs` | secret 与 config 分离 |
| `tests/LanRemote.Security.Tests/DeviceCertificateTests.cs` | 证书、私钥重载、指纹稳定 |
| `tests/LanRemote.Security.Tests/DeviceIdentityTests.cs` | 身份跨重启稳定 |

**修改**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Security/LanRemote.Security.csproj` | 新增 `System.Security.Cryptography.ProtectedData` 10.0.0 与 `Microsoft.Extensions.Logging.Abstractions` 10.0.0 |
| `src/LanRemote.App/App.xaml.cs` | DI 注册 vault / 证书服务 / 身份服务 / `IAccessSecretStore` |
| `src/LanRemote.App/ViewModels/MainViewModel.cs` | 真实身份、证书指纹短前缀、密钥显示/隐藏/复制/重新生成 |
| `src/LanRemote.App/MainWindow.xaml` | 设备码与密钥的复制按钮、密钥显示/隐藏/重新生成、指纹显示、密钥文件状态栏 |
| `src/LanRemote.App/MainWindow.xaml.cs` | 剪贴板操作、重新生成确认框、30 秒后条件清理剪贴板 |
| `README.md` | 状态更新 + secrets.bin 格式说明 |
| `docs/DECISIONS.md` | ADR-014 ~ ADR-017 |

**未改动**：`LanRemote.Core` 的其他文件、`AppConfigStore`、`AppPaths`、`SingleInstanceGuard`、
`SimpleFileLoggerProvider`、Discovery/Transport 常量、M0 全部测试。

## 6. 构建

执行：

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

结果：**PASS**
- 12 个项目全部生成成功
- **0 个警告，0 个错误**
- 注意：本机需要先 `source scripts/env.sh`，否则 `dotnet` 不在 PATH

## 7. 测试

执行：

```text
dotnet test LanRemote.sln -c Debug --no-build
```

结果：**175 passed / 0 failed / 0 skipped**

| 项目 | M0 | 本次新增 | 现在 |
|---|---:|---:|---:|
| LanRemote.Core.Tests | 73 | +46 | 119 |
| LanRemote.Security.Tests | 4 | +42 | 46 |
| LanRemote.Protocol.Tests | 7 | 0 | 7 |
| LanRemote.IntegrationTests | 3 | 0 | 3 |
| **合计** | **87** | **+88** | **175** |

针对 M1 要求清单的测试落点：

| M1 测试要求 | 用例 |
|---|---|
| DeviceCode 同一 identity 稳定 | `DeviceCodeTests.Derive_IsDeterministic`、`DeviceIdentityTests.Identity_IsStableAcrossRestart` |
| 不同 deviceGuid 产生不同身份 | `DeviceCodeTests.Derive_DifferentGuidsProduceDifferentCodes`、`DeviceIdentityTests.Identity_DifferentRootsProduceDifferentDevices` |
| Base32 roundtrip | `CrockfordBase32Tests.RoundTrip_PreservesBytes`（9 种长度）、`RoundTrip_AllOnesBytes` |
| malformed Base32 reject | `TryDecode_RejectsNonCanonicalTrailingBits`、`RejectsInvalidCharacters`、`RejectsEmptyOrBlankInput`、`TryDecodeExact_RejectsWrongLength` |
| Access Key 实际为 128-bit 随机值 | `AccessKeyGenerationTests`（长度/唯一性/无平凡模式）、`DpapiAccessSecretStoreTests.LoadOrCreateAsync_Returns128BitKey` |
| Regenerate 后 Access Key 变化 | `RegenerateAsync_ProducesDifferentKeyImmediately`、`RepeatedRegeneration_ProducesUniqueKeys` |
| DPAPI store roundtrip | `LoadOrCreateAsync_IsStableAcrossRestart`、`RegenerateAsync_IsPersistedAcrossRestart` |
| secrets.bin 不包含 Access Key 明文 | `SecretsFile_DoesNotContainPlaintextKey`（同时查 Base32 串与原始 16 字节） |
| config.json 不包含 secret | `SecretStorageSeparationTests.ConfigJsonNeverContainsSecretMaterial`、`DeviceGuidIsStoredInSecretsBinNotConfigJson` |
| certificate fingerprint 重启一致 | `DeviceCertificateTests.Fingerprint_IsStableAcrossRestart`、`Fingerprint_MatchesCertificateDerHash` |
| certificate private key 可重新加载 | `PrivateKeyCanBeReloadedAfterRestart`、`ReloadedCertificateCanSign`（重载后还能签名，比 `HasPrivateKey` 更强） |
| 日志中不出现 Access Key | `DpapiAccessSecretStoreTests.SecretNeverAppearsInLogs`（捕获消息 + 异常消息） |

## 8. 手工验证

本次**实际执行过**的（在真实 `%LOCALAPPDATA%\LanRemote` 上）：

- [x] **两次冷启动，identity 完全一致**
  - 第 1 次：`首次运行：已创建本机身份与 secrets.bin` → `已生成自签名 ECDSA P-256 设备证书…指纹前缀=3A3D791A` → `设备码=QPKE-2CPC`
  - 第 2 次：`已加载 secrets.bin，版本=1` → `已从 secrets.bin 加载设备证书。指纹前缀=3A3D791A` → `设备码=QPKE-2CPC`
  - 两次进程均存活到超时被杀（exit=124），无崩溃
- [x] **secrets.bin 落盘且无明文**：1807 字节，头部 `4C 52 53 43 01 00 00 07 06`（magic `LRSC`、version 1）；
      全文检索 `accessKey` / `deviceGuid` / `certificatePfx` / `QPKE` / `LanRemote-` **均无命中**
- [x] **日志不含密钥**：扫描日志中长度 ≥20 的 token 共 4 个，全部为文件路径与类名，
      没有任何由 Base32 字母表构成的串
- [x] 数据目录中 `secrets.bin` 与 `config.json` 分属不同文件（本次未触发 config 写入，因为没改动开关）

本次**未执行**的（以及原因）：

- [ ] **UI 上的「显示 / 复制 / 重新生成」按钮点击**：没有可用的 UI 自动化框架，
      **不做伪装的「已验证」声明**。其底层路径（`IAccessSecretStore.RegenerateAsync`、
      `MainViewModel.ToggleAccessKeyVisibilityAsync`、`GetAccessKeyForClipboardAsync`）由单元测试覆盖，
      但**按钮到方法的 WPF 事件连线只做了编译期与代码走查验证**
- [ ] 主窗口实际渲染截图（同上，无 UI 自动化）
- [ ] 换 Windows 用户后 DPAPI 解不开的行为（预期抛 `CryptographicException`，UI 已按此分支提示，但未实测）

## 9. 协议变化

- **没有**。M1 不涉及 wire protocol，未新增/修改任何消息或字段。
- 新增的只是**本机存储格式**：`secrets.bin` 的二进制布局（见 README 与第 12 节）。
- 设备证书未来会参与 M3 的 TLS pinning（发现包里的 `certSha256` 字段已在 `04_PROTOCOL_AND_SECURITY.md` 定义），
  本里程碑只保证指纹可稳定生成与重载。

## 10. 安全影响

- 是否涉及 access key：**是**
  - 生成：`RandomNumberGenerator.GetBytes(16)`，**没有** `Random()`、**没有**固定密码、**没有** 6 位 PIN
  - 存储：DPAPI CurrentUser 保护，落在 `secrets.bin`，**没有**进 `config.json`
  - 传输：尚未联网（M3），届时仍只发 HMAC proof，不发明文 key
  - 日志：Security 层与 ViewModel 两侧都不写密钥；有测试 + 手工扫描
  - 生命周期：显示/复制时临时解码，用完后 `CryptographicOperations.ZeroMemory` 原始字节数组
- 是否涉及 TLS：**间接是**。M1 生成了 M3 要用的证书；证书本身未参与任何传输
- 是否涉及 subnet：**否**
- 是否新增 secret/log：**是**，新增了 `secrets.bin` 这一 secret 载体；日志新增了
  设备码、证书指纹前缀、证书创建/加载事件——这些都不是秘密
- 风险与处理：DPAPI CurrentUser 意味着同用户同机器可解；若机器已完全失陷则无法防御，
  这与 `04_PROTOCOL_AND_SECURITY.md` 第 1 节「不声称防御」的边界一致

## 11. 已知问题 / 技术债

1. **`PersistKeySet` 会在用户 CNG 密钥容器里留下条目**（ADR-016）。单元测试反复创建证书会累积容器；
   M3 如果验证不需要 PersistKeySet，应去掉并更新 ADR。
2. **UI 交互没有自动化覆盖**。按钮事件、剪贴板行为、MessageBox 确认框都只做了代码走查。
   如果想覆盖，最省事的路子是把 ViewModel 逻辑挪到一个可引用的纯类库再测——但这会改动 M0 结构，
   我没有擅自做（用户要求「不要重新设计项目」）。
3. **密钥字符串无法真正清零**：`string` 不可变，隐藏时只能丢弃引用。已用 `ZeroMemory` 处理原始字节数组，
   但进程内存里是否残留副本取决于 GC。
4. **`secrets.bin` 损坏时不自愈**，会抛 `InvalidDataException` 并让 UI 进入错误状态。
   这是刻意选择（不能把「损坏」伪装成「首次运行」），但用户侧还没有「重置本机身份」的入口——M9 时应补一个明确入口。
5. 遗留自 M0：**日志无轮转**（ADR-013，M9）、SDK 不在系统 PATH（用 `scripts/env.sh`）。
6. 遗留自 M0：`LanRemote.Sessions`、`LanRemote.Capture`、`LanRemote.Input` 仍是空项目占位。

## 12. M2 的精确下一步

来源：`07_MILESTONES_AND_TASKS.md` → **M2 — 网卡筛选 + UDP 发现**。按顺序：

1. 在 `src/LanRemote.Discovery` 新建 `NetworkInterfaceSelector`：
   只接受 IPv4、Ethernet/Wireless80211、`OperationalStatus.Up`、有 IPv4 掩码、
   且地址属于 RFC1918（10/8、172.16/12、192.168/16）；
   排除 Loopback、Tunnel、169.254/16、公网地址。规则见 `04_PROTOCOL_AND_SECURITY.md` 第 3 节。
2. 实现 `ISubnetPolicy`（接口已在 `LanRemote.Core.Abstractions`，**不要重新定义**）：
   按真实掩码计算网络号比较，不要只比地址前缀字符串。
   数据驱动用例已在 `08_TEST_PLAN.md` 第 1 节列好（192.168.1.10/24 vs .2.20=false、172.16 vs 172.32=false 等）。
3. 实现 `IDiscoveryService`（接口已在 Core）：
   组播 `239.255.77.77:45872` TTL=1 + 每网卡 directed broadcast probe；
   每 2 秒 announce、缓存 TTL 7 秒、自身公告去重。常量用已有的 `DiscoveryConstants`。
4. 发现报文用 `04_PROTOCOL_AND_SECURITY.md` 第 5.1 节的 JSON；注意 `certSha256` 字段
   现在可以从 `DeviceCertificateService` 拿到真实指纹（M1 已就绪）。
5. MainWindow 设备列表接真实数据（当前是占位的 `ListBoxItem`）。
6. 补测试：RFC1918 判定、掩码/子网、自身公告去重、畸形发现 JSON 不崩。
7. 手工：两台同子网机器 3~6 秒互相看到；停止 app 后 6~8 秒离线。

**M2 不要碰**：TLS / 认证挑战 / 视频 / 键鼠控制——那分别是 M3 / M4 / M5~M7。

## 13. 下一位 AI 不要重复做

- **不要重新实现** `CrockfordBase32`、`DeviceCode`、`DpapiSecretVault`、`DpapiAccessSecretStore`、
  `DeviceCertificateService`、`DeviceIdentityService`——它们已完成且有 88 条测试守护
- **不要把 `IAccessSecretStore` 改回同步签名**（ADR-011）
- **不要把设备码算法改成别的哈希/长度**：改了所有已存在的设备码都会变，等同换身份（ADR-017）
- **不要把 PFX 口令当成一层独立加密**：它是 API 要求，机密性由 DPAPI 提供（ADR-015）
- **不要为了「让 tests 好跑」跳过 DPAPI**：AGENTS.md 第 12 条明令禁止改弱 production policy；
  需要隔离时用 `AppPaths.FromRootDirectory(tempDir)`，这是 `DpapiSecretVault` 已经支持的方式
- **不要在 `config.json` 里加任何 secret 字段**：有测试会红
- **不要删除** `SecretFile.UnpackProtected` 的损坏检测去换取「自愈」：那是刻意设计
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出

## 14. 关键上下文（不知道就容易做错）

1. **`dotnet` 不在 PATH**：先 `source scripts/env.sh`。
2. **`ProtectedData` 需要显式 NuGet 包**：即使 TFM 是 `net10.0-windows`，不引用
   `System.Security.Cryptography.ProtectedData` 就编译不过——这是 M1 踩过的坑。
3. **`ProtectedData.Protect` 的参数名是 `optionalEntropy` 不是 `entropy`**；
   `X509CertificateLoader.LoadPkcs12` 只有 3 个参数，没有 `loaderOptions`，且旧的
   `new X509Certificate2(bytes, pwd, flags)` 已标记过时（SYSLIB0057）。
4. **`X509Certificate2.NotBefore/NotAfter` 返回本地时间**，直接与 `DateTime.UtcNow` 比较
   在 UTC+8 机器上会误判（这次就是这么翻的车），必须先 `.ToUniversalTime()`。
5. **Crockford Base32 解码会拒绝「尾部填充位非 0」的写法**：这是刻意的规范形式校验，
   不是 bug；`TryDecode("ZY")` 会返回 false。
6. **8 个 Base32 字符 = 5 字节**，不是 1 字节。写测试时注意长度换算。
7. **`DpapiSecretVault` 会缓存 bundle**：想模拟「重启」必须新建 `DpapiSecretVault` 实例
   （只换 `DpapiAccessSecretStore` 不生效）。
8. **M2 的 `ISubnetPolicy` 接口已经在 Core 里定义好了**，实现放在 Discovery，别再建一个同名接口。
