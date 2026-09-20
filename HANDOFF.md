# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-20 13:40 (+08:00)**

---

## 1. 当前状态

- **当前里程碑：M1.2 — M1 Final Cleanup，已完成**
- 已完成：M0（仓库骨架）→ M1（设备身份与安全存储）→ M1.1（安全审计）→ **M1.2（封板清理）**
- 版本：`0.1.0-m1`
- **Last code commit：`104f296`**（M1.2 代码 + 测试 + `.gitattributes`）
- **Working tree at validation: clean**
- 总体状态：**M1 系列封板**。按用户指令，**下一阶段是 M2**，不要再打磨 M1

### 关于 git 记账方式（HANDOFF 不再写 HEAD hash）

HANDOFF 本身会被单独提交，如果把「包含这个 HANDOFF 的 commit hash」写回 HANDOFF，
就会形成自引用：写完的那个 hash 立刻过期。因此从 M1.2 起改为两个字段：

- `Last code commit`：最后一次**代码/测试**提交（不含文档提交）
- `Working tree at validation`：验证时的工作区状态

允许 `HEAD` 比 `Last code commit` 新（因为之后可能有单独的 HANDOFF/README 文档提交）。
本轮验证时：`Last code commit = 104f296`，之后追加了文档提交，工作区 clean。

## 2. M1.2 修了什么

| # | 项目 | 结论 |
|---|---|---|
| 1 | `DpapiSecretVault.ReadAsync` 暴露可变缓存实例 | **已改为发放 `Clone()`** |
| 2 | 已存在证书时仍重写 `secrets.bin` | **已改为零写入**（只读加载路径） |
| 3 | `NewPfxPassword` 原始随机 byte[] 未清零 | **已清零** |
| 4 | `TryDecodeExact` 失败返回部分解码字节 | **已改为 ZeroMemory + `Array.Empty`** |
| 5 | `RepairAndMaterialize` 用 `out byte[] _` 丢弃有效密钥字节 | **已改为探针用后清零** |
| 6 | partial certificate 测试只能证明「错误口令会失败」 | **已重写为真正证明原证书未被替换** |

### 2.1 ReadAsync 是否已改 clone —— ✅ 是

```csharp
SecretBundle bundle = await EnsureLoadedCoreAsync(cancellationToken);
return projection(bundle.Clone());   // 交出去的是副本
```

- projection 对副本的任何修改都影响不到 `_cached`，更影响不到磁盘
- `DpapiSecretVault` 的 XML 注释已同步更新，与「外部拿不到 cached mutable instance」真正一致
- 代价：每次读多一次浅拷贝（字段都是值 / Guid / string，开销可忽略）
- 回归测试：`DpapiSecretVaultTransactionTests.ReadProjectionMutation_DoesNotChangeCacheOrDisk`
  （读 A → projection 内改成 `"TAMPERED"` → 同 vault 仍为 A → 新 vault 从磁盘仍为 A）

### 2.2 已存在证书启动是否零写入 —— ✅ 是

`DeviceCertificateService.GetOrCreateAsync` 现在分两阶段：

```text
阶段 1（只读，不写文件）：vault.ReadAsync(SnapshotCertificate)
    PFX + 口令都在  → 直接 ImportFromPfx 加载，返回缓存，不触碰 secrets.bin
    只有一个存在    → throw InvalidDataException（fail closed，绝不重签）
    两个都不存在    → 进入阶段 2

阶段 2（写）：vault.UpdateAsync(CreateOrLoadCore)
    CreateOrLoadCore 内【再次】检查状态：
        已在（竞态兜底）→ 直接用那一份，绝不覆盖
        半损坏         → throw InvalidDataException
        确实没有       → 签发并持久化
```

- `_gate` 保留未动
- 回归测试：`DeviceCertificateTests.ExistingCertificate_LoadDoesNotRewriteSecretsFile`
  （创建 → 保存文件字节 → 新建 vault + service 模拟冷启动 → 再加载 → **byte-for-byte 完全一致** + 指纹相同）
- **手工验证也做了**：真实 `%LOCALAPPDATA%\LanRemote\secrets.bin` 在启动前后
  `sha256` 与 `mtime` **均未变化**（`83ec595f…`，mtime 1789881661，1807 字节），
  日志走的是新分支：`已从 secrets.bin 加载设备证书（未重写文件）。指纹前缀=3A3D791A`

### 2.3 NewPfxPassword byte[] 清零 —— ✅

```csharp
byte[] passwordBytes = RandomNumberGenerator.GetBytes(PfxPasswordByteCount);
try { return Convert.ToBase64String(passwordBytes); }
finally { CryptographicOperations.ZeroMemory(passwordBytes); }
```

### 2.4 TryDecodeExact failure semantics —— ✅

Base32 合法但解码长度 ≠ 期望长度时：

1. `CryptographicOperations.ZeroMemory(bytes)`
2. `bytes = Array.Empty<byte>()`
3. `return false`

目的：M4 校验用户输入的访问密钥时，失败路径不能把「部分解码出的秘密字节」留给调用方。
Core 为此新增 `using System.Security.Cryptography;` —— 只用 BCL，**未新增任何第三方包**。

测试：`CrockfordBase32Tests.TryDecodeExact_LengthMismatch_ReturnsEmptyInsteadOfPartialSecret`
（4 种长度）+ `TryDecodeExact_MatchingLength_ReturnsDecodedBytes` + `…_MalformedInput_AlsoReturnsEmpty`。

### 2.5 partial certificate 恢复测试 —— ✅ 已加强

新流程：生成证书记下指纹 A → 读出**原来的** `CertificatePfxPassword` → 置 null →
新 service 必须 `InvalidDataException` → **恢复原口令** → 新 service 再加载 → 指纹仍等于 A。
最后还断言 `_logs.Contains(originalPassword) == false`（口令不得进日志）。

旧版本最后一步恢复的是一个**新的随机口令**，只能证明「错误口令会失败」，
并不能证明原证书没有被悄悄替换；现在这条才真正闭环。

### 2.6 RepairAndMaterialize

```csharp
byte[] probe = Array.Empty<byte>();
try { if (!TryDecodeAccessKey(bundle.AccessKey, out probe)) bundle.AccessKey = NewAccessKey(); }
finally { CryptographicOperations.ZeroMemory(probe); }
return Materialize(bundle.AccessKey);
```

不再出现 `TryDecodeAccessKey(..., out byte[] _)` 把有效密钥字节丢给 GC 的写法。

## 3. 本轮明确没有施工

未实现、未触碰：NetworkInterfaceSelector、SubnetPolicy、UDP Discovery、TLS、Auth、Video、Input、Session。
这仍然是 M1 封板。

## 4. 修改文件

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Security/Secrets/DpapiSecretVault.cs` | `ReadAsync` 发 `Clone()`；XML 注释同步 |
| `src/LanRemote.Security/Certificates/DeviceCertificateService.cs` | 两阶段加载；新增 `CertificateBundleState` / `CertificateSnapshot` / `CorruptStateMessage`；写路径内二次检查 |
| `src/LanRemote.Security/Secrets/SecretGenerator.cs` | `NewPfxPassword` 清零原始字节 |
| `src/LanRemote.Security/Secrets/DpapiAccessSecretStore.cs` | `RepairAndMaterialize` 探针清零 |
| `src/LanRemote.Core/Encoding/CrockfordBase32.cs` | `TryDecodeExact` 失败语义 + `System.Security.Cryptography` using |
| `tests/LanRemote.Security.Tests/DpapiSecretVaultTransactionTests.cs` | 新增 `ReadProjectionMutation_DoesNotChangeCacheOrDisk` |
| `tests/LanRemote.Security.Tests/DeviceCertificateTests.cs` | 新增 `ExistingCertificate_LoadDoesNotRewriteSecretsFile`；重写 partial 恢复测试 |
| `tests/LanRemote.Core.Tests/CrockfordBase32Tests.cs` | 新增 3 条 TryDecodeExact 用例 |
| `.gitattributes`（新增） | `.workbuddy/ export-ignore` + 二进制声明 |
| `.gitignore` | 忽略生成的 `LanRemote-source.zip` |

## 5. 构建

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

结果：**PASS** —— 12 个项目全部生成，**0 个警告，0 个错误**

## 6. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

结果：**197 passed / 0 failed / 0 skipped**

| 项目 | M1.1 后 | M1.2 后 | 增量 |
|---|---:|---:|---:|
| LanRemote.Core.Tests | 119 | **125** | +6 |
| LanRemote.Security.Tests | 60 | **62** | +2 |
| LanRemote.Protocol.Tests | 7 | 7 | 0 |
| LanRemote.IntegrationTests | 3 | 3 | 0 |
| **合计** | **189** | **197** | **+8** |

既有 189 条全部继续通过；没有修改任何测试来掩盖 production bug。

## 7. 源码审计包

```text
git archive --format=zip -o LanRemote-source.zip HEAD
```

- **已生成**：`LanRemote-source.zip`（仓库根目录）
- `.gitattributes` 用 `export-ignore` 排除了 `.workbuddy/`
  （里面有测试机路径、设备码、证书指纹等开发环境信息——不是 Access Key，但不属于产品源码）
- `git archive` 天然不含 `.git`；`bin` / `obj` / `TestResults` / 真实 `secrets.bin` / `logs` /
  `*.pfx` / `*.key` 都属于被 `.gitignore` 忽略的未跟踪文件，不会进包
- `LanRemote-source.zip` 本身也已加入 `.gitignore`，不会污染工作区

## 8. 手工验证

- [x] **已存在证书时零写入**：`secrets.bin` 的 sha256 与 mtime 在应用启动前后完全一致
- [x] 应用启动无崩溃（exit=124 表示存活到超时被杀）
- [ ] UI 按钮点击仍未做（无 UI 自动化框架），**不伪装成已验证**
- [ ] 换 Windows 用户后 DPAPI 解不开的行为仍未实测

## 9. 已知问题 / 技术债（累计）

1. 日志没有轮转（M9 必修，ADR-013）
2. UI 交互无自动化覆盖
3. `string` 无法可靠清零（.NET 限制，已如实记录）
4. `secrets.bin` 损坏时显式失败、无自愈入口（M9 应补「重置本机身份」）
5. M1 期间单元测试用 `PersistKeySet` 建的证书可能在用户 CNG 密钥容器留有条目（M1.1 起不再新增）
6. `LanRemote.Sessions` / `Capture` / `Input` 仍是空项目占位
7. **ADR-018 的未关闭风险**：Windows SslStream 服务端用 ephemeral 私钥是否可靠没有实测，
   **M3 必须补真实握手集成测试**再下结论

## 10. 下一步 —— M2（网卡筛选 + UDP 发现）

按 `07_MILESTONES_AND_TASKS.md`：

1. `NetworkInterfaceSelector`：IPv4、Ethernet/Wireless80211、`Up`、有掩码、RFC1918；
   排除 Loopback / Tunnel / 169.254 / 公网
2. 实现 `ISubnetPolicy`（接口已在 `LanRemote.Core.Abstractions`，**不要重新定义**）——按真实掩码比较网络号
3. 实现 `IDiscoveryService`：组播 `239.255.77.77:45872` TTL=1 + directed broadcast probe；
   2 秒 announce、7 秒缓存 TTL；自身公告去重
4. 发现报文的 `certSha256` 字段可从 `DeviceCertificateService` 拿到真实指纹
5. MainWindow 设备列表接真实数据
6. M2 不要碰 TLS / Auth / 视频 / 键鼠

## 11. 下一位 AI 不要重复做

- **不要再改 M1 的东西**：M1.2 是封板，下一步就是 M2
- **不要把 `ReadAsync` 改回发放缓存实例**（那是 M1.2 刚修掉的洞）
- **不要把证书加载改回「无条件 UpdateAsync」**（会重新引入无谓写盘）
- **不要把 `PersistKeySet` / `Exportable` 加回来**，除非 M3 的真实 SslStream 握手测试证明必须（ADR-018）
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出
- 不要重新实现 `CrockfordBase32`、`DeviceCode`、`DpapiSecretVault`、`DpapiAccessSecretStore`、
  `DeviceCertificateService`、`DeviceIdentityService`

## 12. 关键上下文

1. **`dotnet` 不在 PATH**：先 `source scripts/env.sh`
2. **`ProtectedData` 需要显式 NuGet 包**，即使 TFM 是 `net10.0-windows`；参数名是 `optionalEntropy`
3. **`X509CertificateLoader.LoadPkcs12` 只有 3 个参数**；旧的 `new X509Certificate2(bytes, pwd, flags)` 已过时
4. **`X509Certificate2.NotBefore/NotAfter` 返回本地时间**，与 `DateTime.UtcNow` 比较前要 `.ToUniversalTime()`
5. **模拟「重启」必须新建 `DpapiSecretVault`**（有缓存）
6. **`EphemeralKeySet` 下不能导出私钥**：需要 PFX 时必须走「新建 → Export → 存 bundle」
7. **`UpdateAsync` 是唯一写入通道，`ReadAsync` 只发副本**：两条路径都改不了真实状态
8. **`TryDecodeExact` 失败时 out 参数是 `Array.Empty<byte>()`**，不是部分解码结果
9. **8 个 Base32 字符 = 5 字节**，不是 1 字节
10. **HANDOFF 不写 HEAD hash**，写 `Last code commit` + `Working tree at validation`
