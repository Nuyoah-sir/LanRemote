# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-20 13:25 (+08:00)**

---

## 1. 当前状态

- **当前里程碑：M1.1 — Security Hardening（M1 的审计修复），已完成**
- 已完成里程碑：M0（仓库骨架）→ M1（设备身份与安全存储）→ **M1.1（安全审计修复）**
- 版本：`0.1.0-m1`（`Directory.Build.props` 的 `LanRemoteVersion`）
- 分支/commit：**`main` @ `664e558`**（首个基线提交，97 个文件；工作区干净）
- 总体状态：**里程碑完成**。按用户指令，**不进入 M2**

## 2. M1.1 修了哪些问题

| # | 问题 | 修复 | 证据 |
|---|---|---|---|
| 1 | 证书私钥导入用了 `PersistKeySet \| Exportable`，把私钥额外持久化进用户 CNG 密钥容器，并标记为可导出 | 改为 `X509KeyStorageFlags.EphemeralKeySet`；常量公开为 `DeviceCertificateService.ImportFlags` | `DeviceCertificateTests.ImportFlags_UsesEphemeralKeySetOnly`、`ReloadedCertificateCanSign` |
| 2 | `UpdateAsync` 把缓存实例直接交给 operation 修改，**先改内存、后落盘**，失败时会出现「内存是新 key、磁盘是旧 key」 | 改为 copy-on-write（见第 5 节） | `DpapiSecretVaultTransactionTests` 共 5 条 |
| 3 | 秘密 `byte[]` 用完后不清零（访问密钥原始字节、PFX 导出缓冲、PFX 导入缓冲、轮换出的新密钥） | 全部在 `finally` 中 `CryptographicOperations.ZeroMemory` | 见第 6 节 |
| 4 | 证书字段「半损坏」（PFX 与口令只有一个存在）时会**静默重新签发证书**，导致指纹悄悄改变 | fail closed：抛 `InvalidDataException`，不生成新证书 | `CertificateState_PfxWithoutPassword_IsRejected`、`CertificateState_PasswordWithoutPfx_IsRejected` |
| 5 | `UnpackProtected` 只检查 `actualLength < declaredLength`，被追加过内容的文件能蒙混过关；也不校验 payload 内的 bundle 版本 | 改为**严格相等** + 解密后校验 `bundle.Version == CurrentVersion` | `SecretFileFormatTests` 共 5 条 |

**安全要求未变**（逐项确认）：访问密钥仍为 `RandomNumberGenerator.GetBytes(16)`；DPAPI 仍为 CurrentUser；
密钥不进 config.json；密钥不进日志；DeviceCode 算法未动；证书仍为 ECDSA P-256；
fingerprint 算法（对 `RawData` 求 SHA-256）未动。**未**施工 Discovery / TLS / Auth / Video / Input。

## 3. 最终证书 key-storage flags

```csharp
public const X509KeyStorageFlags ImportFlags = X509KeyStorageFlags.EphemeralKeySet;
```

- ❌ `PersistKeySet` —— 不再把私钥写入用户 CNG 密钥容器
- ❌ `Exportable` —— 不再把导入的私钥标记为可导出
- ✅ 磁盘上的唯一持久化副本 = DPAPI 保护的 `secrets.bin`
- ✅ 进程运行期间私钥只在内存，进程退出即消失

**未关闭的风险（必须留给 M3）**：Windows 上 SslStream 服务端使用 ephemeral 私钥是否可靠，
M1/M1.1 阶段**没有真实 TLS 测试可以证明**。因此 ADR-018 附带强制要求：
**M3 必须新增真实 SslStream server/client 握手集成测试**，用实测结果决定维持或回退。
在拿到实测结果之前，**不得**凭猜测改回 `PersistKeySet`。

## 4. ADR 状态

- **ADR-016 —— ✅ 已标记 `SUPERSEDED by ADR-018`**（`docs/DECISIONS.md` 标题已加删除线与作废日期，
  正文保留以备追溯，并明确写「请勿照此实现」）
- **ADR-018（新增）**：私钥导入改为 `EphemeralKeySet`，取代 ADR-016；含 M3 强制验证要求
- **ADR-019（新增）**：`UpdateAsync` copy-on-write 事务语义
- **ADR-020（新增）**：证书 bundle 状态 fail closed + `secrets.bin` 严格长度/版本校验

## 5. copy-on-write 的实现方式

`DpapiSecretVault.UpdateAsync<TResult>` 现在的严格顺序：

```text
1. 读取当前缓存的 bundle（current）
2. SecretBundle.Clone()  → 独立的 working copy
3. operation(working)    → 只允许改 working
4. PersistCoreAsync(working)   ← 先落盘
5. _cached = working           ← 只有上一步成功才替换缓存
```

要点：

- `SecretBundle` 新增 `Clone()` 深拷贝（所有字段都是值/Guid/string，无共享引用）
- `PersistCoreAsync` 在动手写临时文件**之前**先 `cancellationToken.ThrowIfCancellationRequested()`，
  让「取消 → 磁盘保持旧状态」成为确定性行为，而不是取决于 `File.WriteAllBytesAsync` 什么时候察觉
- 取消 / `IOException` / `UnauthorizedAccessException` / DPAPI 失败等**任意**异常下：
  磁盘保持旧状态，且 `_cached` 也保持旧状态，二者不可能撕裂
- 失败时清理临时文件，不会留下 `*.tmp`

## 6. 秘密 byte[] 生命周期清理

| 位置 | 处理 |
|---|---|
| `SecretGenerator.NewAccessKey()` | 生成 16 字节 → Base32 编码 → `finally` 清零原始字节 |
| `DeviceCertificateService.CreateAndStoreCore` | `Export(Pfx)` 的 byte[] → `ToBase64String` 后 `finally` 清零 |
| `DeviceCertificateService.ImportFromPfx` | `FromBase64String` 的 PFX byte[] → 载入成功**或失败**都 `finally` 清零 |
| `MainViewModel.RegenerateAccessKeyAsync()` | 接收 `RegenerateAsync()` 返回的 `AccessSecret` 到局部变量，`finally` 清零其原始字节 |

**诚实说明（.NET 语言级限制，不是偷懒）**：
`string` 不可变，因此 Base32 展示串、PFX 口令串**无法**可靠清零——它们一旦存在，
进程内存中是否残留副本取决于 GC。已清零的是所有我们能控制的原始 `byte[]`。

## 7. 新增的失败路径测试

`tests/LanRemote.Security.Tests/DpapiSecretVaultTransactionTests.cs`（5 条）

1. `CancelledPersist_LeavesDiskAndMemoryUnchanged` —— operation 内改 working copy 后主动 cancel；
   `UpdateAsync` 抛 `OperationCanceledException`；**同一个 vault** 读回仍是 A；**全新 vault** 从磁盘读回仍是 A
2. `AccessKeyRotation_FailedPersist_KeepsOldKeyServable` —— access key rotation 走同一条 `UpdateAsync` 路径，
   取消后 `IAccessSecretStore.LoadOrCreateAsync()` 仍返回旧 key（重启后亦然）
3. `SuccessfulUpdate_IsVisibleToFreshVault` —— 反向对照：成功时新值确实对新 vault 可见
4. `Clone_ProducesIndependentCopy` —— 深拷贝互不影响
5. `CancelledPersist_DoesNotLeaveTemporaryFiles` —— 不留 `.tmp`

`tests/LanRemote.Security.Tests/SecretFileFormatTests.cs`（5 条）

1. `AppendedTrailingBytes_AreRejected` —— 合法文件后追加 3 字节 → 必须拒绝
2. `TruncatedFile_IsRejected`
3. `BundleVersionMismatchInsidePayload_IsRejected` —— 手工构造「头部版本正确、payload 内 version=99」的文件
4. `WrongHeaderVersion_IsRejected`
5. `PackProtected_SetsCurrentVersion`

`tests/LanRemote.Security.Tests/DeviceCertificateTests.cs`（新增 3 条）

1. `CertificateState_PfxWithoutPassword_IsRejected`
2. `CertificateState_PasswordWithoutPfx_IsRejected`
3. `CertificateState_PartialStateDoesNotSilentlyReissue` —— 破坏后不会静默换证书
4. `ImportFlags_UsesEphemeralKeySetOnly` —— 防止以后把 flags 加回来

**被修改的现有测试（唯一一处）**：`GetOrCreateAsync_UsesEcdsaP256` 原来用
`ExportParameters(includePrivateParameters: true)` 确认曲线；按 M1.1 要求改为只用
**公钥/证书公开信息**（`GetECDsaPublicKey()` + `ExportParameters(false)` + `KeySize`）确认 P-256。
这是按指令调整断言方式，**不是**为了掩盖 production bug；`ReloadedCertificateCanSign`
（重载后签名 + 公钥验签）按要求保留并通过。

## 8. 构建

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

结果：**PASS**
- 12 个项目全部生成成功
- **0 个警告，0 个错误**

## 9. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

结果：**189 passed / 0 failed / 0 skipped**

| 项目 | M1 结束时 | M1.1 后 | 增量 |
|---|---:|---:|---:|
| LanRemote.Core.Tests | 119 | 119 | 0 |
| LanRemote.Security.Tests | 46 | **60** | +14 |
| LanRemote.Protocol.Tests | 7 | 7 | 0 |
| LanRemote.IntegrationTests | 3 | 3 | 0 |
| **合计** | **175** | **189** | **+14** |

既有 175 条**全部继续通过**，未修改任何测试来掩盖 production bug。

## 10. 手工验证

- [x] **M1.1 后的 identity restart 验证：已运行**
  - 在真实 `%LOCALAPPDATA%\LanRemote` 上连续启动两次（都是 exit=124，即存活到超时被杀，无崩溃）
  - 两次都加载的是 **M1 时期落盘的同一份 PFX**：`已从 secrets.bin 加载设备证书。指纹前缀=3A3D791A`、
    `设备码=QPKE-2CPC`
  - 结论：`PersistKeySet → EphemeralKeySet` 的切换**没有**改变指纹、没有触发重新签发
- [ ] **UI 上「显示 / 复制 / 重新生成」按钮点击**：仍未做（无 UI 自动化框架），**不伪装成已验证**
- [ ] 换 Windows 用户后 DPAPI 解不开的行为：仍未实测

## 11. git

- [x] `git init` 已执行；`.gitignore` 生效（`git status --ignored` 确认 `bin/`、`obj/` 全部被忽略）
- [x] 已确认**没有**把 `bin`、`obj`、`secrets.bin`、`logs`、`*.pfx` 混入版本库
- [x] 首个基线提交：`664e558`（97 个文件，12550 行），工作区干净
- 说明：`.workbuddy/memory/` 也被纳入版本库（它是本项目的长期记忆，不是临时缓存）

## 12. 发现但**未**修的问题（留给你决定）

按「不顺便施工」的约束，以下只记录、未改动代码：

1. **`ReadAsync` 把缓存实例直接交给投影函数**（严重度：低，但属于同类隐患）。
   如果某个调用方在投影里改了 bundle，就会造成「内存改了、磁盘没改」——正好是本次在
   `UpdateAsync` 里修掉的那种撕裂状态。当前所有调用方都只读。
   建议后续把 `ReadAsync` 也改成发放 `Clone()`（一行改动，代价是每次读多一次分配）。
2. **`GetOrCreateAsync` 每次调用都会触发一次文件写入**。`DeviceCertificateService` 走的是
   `vault.UpdateAsync`，而 `UpdateAsync` 无论内容是否变化都会 persist。证书已存在时
   这次写入是多余的 DPAPI 往返。语义正确，只是浪费；建议 M3 之前加一个「无变更则不写」的短路。
3. **证书状态损坏后没有自愈入口**。fail closed 之后用户会看到错误，但当前没有
   「重置本机身份」的 UI 入口。M9 应补（这条在 M1 HANDOFF 里也记过）。
4. 遗留自 M0：**日志无轮转**（ADR-013）、**SDK 不在系统 PATH**（用 `scripts/env.sh`）。

## 13. 已知问题 / 技术债（累计）

1. 日志没有轮转（M9 必修，ADR-013）
2. UI 交互无自动化覆盖（M1.1 仍未解决）
3. `string` 无法可靠清零（.NET 限制，已诚实记录）
4. `secrets.bin` 损坏时显式失败、无自愈入口
5. `PersistKeySet` 遗留副作用：**M1 期间单元测试创建过的证书在用户 CNG 密钥容器里可能留有条目**；
   M1.1 之后不会再新增。若在意，可用 `certmgr`/CNG 工具清理
6. `LanRemote.Sessions` / `Capture` / `Input` 仍是空项目占位

## 14. 下一步（仍在 M2 之前；当前按指令停在这里）

用户明确要求 **M1.1 完成后停止，不进入 M2**。下次开工请从 `07_MILESTONES_AND_TASKS.md` 的
**M2 — 网卡筛选 + UDP 发现**开始：

1. `NetworkInterfaceSelector`：IPv4、Ethernet/Wireless80211、`Up`、有掩码、RFC1918；排除 Loopback/Tunnel/169.254
2. 实现 `ISubnetPolicy`（接口已在 `LanRemote.Core.Abstractions`，**不要重新定义**）——按真实掩码比较网络号
3. 实现 `IDiscoveryService`：组播 `239.255.77.77:45872` TTL=1 + directed broadcast probe；2 秒 announce、7 秒缓存
4. 发现报文的 `certSha256` 字段现在可以从 `DeviceCertificateService` 拿到真实指纹
5. MainWindow 设备列表接真实数据
6. M2 **不要**碰 TLS / Auth / 视频 / 键鼠

## 15. 下一位 AI 不要重复做

- **不要把 `PersistKeySet`/`Exportable` 加回来**，除非 M3 的真实 SslStream 握手测试证明必须（ADR-018）
- **不要为了「让测试好过」改 production 的安全策略**（AGENTS.md 第 12 条）
- **不要把证书 partial state 改成「自动生成新证书」**：那会静默改变指纹（ADR-020）
- **不要把 `UnpackProtected` 的长度校验放松回 `<=`**
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出
- 不要重新实现 `CrockfordBase32`、`DeviceCode`、`DpapiSecretVault`、`DpapiAccessSecretStore`、
  `DeviceCertificateService`、`DeviceIdentityService`

## 16. 关键上下文

1. **`dotnet` 不在 PATH**：先 `source scripts/env.sh`
2. **`ProtectedData` 需要显式 NuGet 包**，即使 TFM 是 `net10.0-windows`
3. **`ProtectedData.Protect` 参数名是 `optionalEntropy`**；`X509CertificateLoader.LoadPkcs12` 只有 3 个参数
4. **`X509Certificate2.NotBefore/NotAfter` 返回本地时间**，与 `DateTime.UtcNow` 比较前要 `.ToUniversalTime()`
5. **模拟「重启」必须新建 `DpapiSecretVault`**（有缓存）
6. **`EphemeralKeySet` 下不能导出私钥**：需要 PFX 时必须走「新建 → Export → 存 bundle」，
   不能从已加载的证书 `Export`
7. **`UpdateAsync` 是唯一的写入通道**：`ReadAsync` 拿到的实例理论上是可变的，别在里面改东西
   （见第 12 节第 1 条）
8. **8 个 Base32 字符 = 5 字节**，不是 1 字节
