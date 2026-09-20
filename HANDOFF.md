# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-20 13:55 (+08:00)**

---

## 1. 当前状态

- **当前里程碑：M1.3 — ECDSA Certificate KeyUsage Fix，已完成**
- 已完成：M0（仓库骨架）→ M1（设备身份与安全存储）→ M1.1（安全审计）→
  M1.2（封板清理）→ **M1.3（证书 KeyUsage 修复）**
- 版本：`0.1.0-m1`
- **Last code commit：`9e75fce`**（M1.3 代码 + 测试）
- **Working tree at validation: clean**
- 总体状态：**M1 正式封板**。按用户指令，**下一阶段就是 M2，不要再打磨 M1**

### 关于 git 记账方式（HANDOFF 不写 HEAD hash）

HANDOFF 本身会被单独提交，把「包含这个 HANDOFF 的 commit hash」写回 HANDOFF 会形成自引用：
写完那个 hash 立刻过期。因此固定使用两个字段：

- `Last code commit`：最后一次**代码/测试**提交（不含文档提交）
- `Working tree at validation`：验证时的工作区状态

允许 `HEAD` 比 `Last code commit` 新（之后会有单独的 HANDOFF/README 文档提交）。

## 2. M1.3 做了什么

只有一件事：修 ECDSA 证书的 **Key Usage profile**。

### 2.1 修改前 / 修改后

```csharp
// 修改前（错误）
new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true)

// 修改后（最终值）
new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature, critical: true)
```

### 2.2 原因

LanRemote 的设备证书是 **ECDSA / P-256 / `id-ecPublicKey` / end-entity server certificate**，
TLS 服务端身份使用 **ECDSA 签名**。

RFC 5480 对 `id-ecPublicKey` 的 end-entity certificate 允许的 Key Usage 只有：

- `digitalSignature`
- `nonRepudiation`
- `keyAgreement`

`keyEncipherment` 是 RSA 密钥传输语义，**不属于** EC certificate profile。
在 ECDSA 证书里声明它属于 profile 违规，严格校验方可能直接拒绝该证书。

本项目只需要 `digitalSignature`；**刻意不用** `keyAgreement`（那是 ECDH 语义，本项目不做）。

### 2.3 未改动的部分（逐项确认）

| 项 | 状态 |
|---|---|
| ECDSA P-256 | 未动 |
| SHA-256 签名 | 未动 |
| `EphemeralKeySet` 私钥导入（ADR-018） | 未动 |
| 指纹算法（`SHA256(RawData)`） | 未动 |
| PFX + DPAPI 持久化方式 | 未动 |
| 5 年有效期 | 未动 |
| `BasicConstraints CA=false` | 未动 |
| **ServerAuth EKU `1.3.6.1.5.5.7.3.1`** | **保留，未被本次修改破坏** |

## 3. 测试

### 新增（3 条）

| 用例 | 断言 |
|---|---|
| `CertificateKeyUsage_IsDigitalSignatureOnly` | 扩展存在；`Critical == true`；含 `DigitalSignature`；**不含** `KeyEncipherment`；**不含** `KeyAgreement`；最终 `KeyUsages` **恰好等于** `DigitalSignature` |
| `CertificateProfile_MatchesEcdsaServerIdentity` | 组合 profile：ECDSA P-256 + `CA=false` + `KeyUsage == DigitalSignature` + EKU 含 serverAuth + 公钥算法为 `id-ecPublicKey`（1.2.840.10045.2.1，不是 RSA） |
| `KeyUsageSurvivesReloadFromSecretsBin` | 从 PFX 重载（模拟冷启动）后 KeyUsage 仍是 `DigitalSignature` only |

### 保留

`CertificateDeclaresServerAuthenticationUsage` 保留并通过 —— 确认 serverAuth EKU 未被破坏。

## 4. 本机开发 identity 是否重置过 —— ✅ 是（手工、开发阶段）

M1.2 以前签出的开发证书带 `KeyEncipherment`，profile 不合规。既然项目尚未发布、
也还没进 M2/M3，这属于 **pre-release development identity**，因此做了明确的备份 + 重置。

**没有**在 production code 里加入任何「静默自动换证」逻辑 —— 已有证书永远不会被自动替换
（`DeviceCertificateService` 仍然 fail closed，证书字段半损坏会抛异常而不是重签）。

| 项 | 重置前（M1.2 及更早） | 重置后（M1.3） |
|---|---|---|
| DeviceCode | `QPKE-2CPC` | **`M5WC-14GX`（变了）** |
| 证书指纹前缀 | `3A3D791A` | **`89A5C10E`（变了）** |
| secrets.bin 大小 | 1807 字节 | 1791 字节 |
| 旧文件 | — | 已备份到 `%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak` |

重置后连续冷启动两次，日志均为 `设备码=M5WC-14GX，证书指纹前缀=89A5C10E`，
第二次走的是「已从 secrets.bin 加载设备证书（未重写文件）」——新身份稳定。

> 说明：这是**开发阶段的手工 reset**，不是正式的升级迁移逻辑。
> 将来如果项目有了已发布的设备身份，就不能这样做，需要真正的 profile 迁移方案。

## 5. 修改文件

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Security/Certificates/DeviceCertificateService.cs` | KeyUsage 改为 `DigitalSignature`（critical）；注释写明 RFC 5480 依据 |
| `tests/LanRemote.Security.Tests/DeviceCertificateTests.cs` | 新增 3 条 KeyUsage / profile 回归测试 |

## 6. 构建

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

结果：**PASS** —— 12 个项目全部生成，**0 个警告，0 个错误**

## 7. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

结果：**200 passed / 0 failed / 0 skipped**

| 项目 | M1.2 后 | M1.3 后 | 增量 |
|---|---:|---:|---:|
| LanRemote.Core.Tests | 125 | 125 | 0 |
| LanRemote.Security.Tests | 62 | **65** | +3 |
| LanRemote.Protocol.Tests | 7 | 7 | 0 |
| LanRemote.IntegrationTests | 3 | 3 | 0 |
| **合计** | **197** | **200** | **+3** |

既有 197 条全部继续通过；没有修改任何测试来掩盖 production bug。

## 8. 本轮禁止施工的项目 —— 确认未碰

未实现、未触碰：NetworkInterfaceSelector、SubnetPolicy、UDP Discovery、TLS Listener、
SslStream、Auth、Video、Input、Session。

## 9. 源码审计包

```text
git archive --format=zip -o LanRemote-source.zip HEAD
```

已按上一条指令重新生成（在 M1.3 文档提交之后）。`.gitattributes` 仍然用 `export-ignore`
排除 `.workbuddy/`；`git archive` 天然不含 `.git`；`bin`/`obj`/`TestResults`/真实
`secrets.bin`/`logs`/`*.pfx`/`*.key` 都属于未跟踪文件，不会进包。

## 10. 已知问题 / 技术债（累计）

1. 日志没有轮转（M9 必修，ADR-013）
2. UI 交互无自动化覆盖
3. `string` 无法可靠清零（.NET 限制，已如实记录）
4. `secrets.bin` 损坏时显式失败、无自愈入口（M9 应补「重置本机身份」）
5. **ADR-018 的未关闭风险**：Windows SslStream 服务端用 ephemeral 私钥是否可靠没有实测，
   **M3 必须补真实握手集成测试**再下结论
6. `LanRemote.Sessions` / `Capture` / `Input` 仍是空项目占位
7. 本机 `%LOCALAPPDATA%\LanRemote\backups\` 下有一份 M1.3 重置前的旧开发 `secrets.bin`
   （DPAPI 保护，仅开发用；若不再需要可手工删除）

## 11. 下一阶段 —— M2（网卡筛选 + UDP 发现）

按 `07_MILESTONES_AND_TASKS.md`：

1. `NetworkInterfaceSelector`：IPv4、Ethernet/Wireless80211、`Up`、有掩码、RFC1918；
   排除 Loopback / Tunnel / 169.254 / 公网
2. 实现 `ISubnetPolicy`（接口已在 `LanRemote.Core.Abstractions`，**不要重新定义**）——按真实掩码比较网络号
3. 实现 `IDiscoveryService`：组播 `239.255.77.77:45872` TTL=1 + directed broadcast probe；
   2 秒 announce、7 秒缓存 TTL；自身公告去重
4. 发现报文的 `certSha256` 字段可从 `DeviceCertificateService` 拿到真实指纹（当前 `89A5C10E…`）
5. MainWindow 设备列表接真实数据
6. M2 不要碰 TLS / Auth / 视频 / 键鼠

## 12. 下一位 AI 不要重复做

- **不要再打磨 M1**：M1.3 是最后一次修改，下一次施工必须进入 M2
- **不要把 `keyEncipherment` 或 `keyAgreement` 加回 ECDSA 证书**（RFC 5480 profile 违规）
- **不要删 serverAuth EKU**
- **不要加「静默自动重签已有证书」逻辑**：已有证书只能由用户显式轮换
- **不要把 `PersistKeySet` / `Exportable` 加回来**，除非 M3 真实 SslStream 测试证明必须（ADR-018）
- **不要把 `ReadAsync` 改回发放缓存实例**（M1.2 刚修掉）
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出

## 13. 关键上下文

1. **`dotnet` 不在 PATH**：先 `source scripts/env.sh`
2. **`ProtectedData` 需要显式 NuGet 包**，即使 TFM 是 `net10.0-windows`；参数名是 `optionalEntropy`
3. **`X509CertificateLoader.LoadPkcs12` 只有 3 个参数**；旧的 `new X509Certificate2(bytes, pwd, flags)` 已过时
4. **`X509Certificate2.NotBefore/NotAfter` 返回本地时间**，与 `DateTime.UtcNow` 比较前要 `.ToUniversalTime()`
5. **模拟「重启」必须新建 `DpapiSecretVault`**（有缓存）
6. **`EphemeralKeySet` 下不能导出私钥**：需要 PFX 时必须走「新建 → Export → 存 bundle」
7. **`UpdateAsync` 是唯一写入通道，`ReadAsync` 只发副本**
8. **`TryDecodeExact` 失败时 out 是 `Array.Empty<byte>()`**，不是部分解码结果
9. **本机开发身份在 M1.3 被重置过**：设备码 `M5WC-14GX`、指纹 `89A5C10E…`；
   旧身份备份在 `backups/secrets.bin.pre-m1.3-reset.bak`
10. **HANDOFF 不写 HEAD hash**，写 `Last code commit` + `Working tree at validation`
