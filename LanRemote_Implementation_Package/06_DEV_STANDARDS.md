# AI 开发规范

## 1. 总原则

代码优先级：

1. 正确性；
2. 安全边界；
3. 可取消/可释放；
4. 可测试；
5. 可读；
6. 性能；
7. 少量漂亮抽象。

不要为了“高级架构”堆无意义层级。

## 2. C# 风格

- Nullable enabled；
- async 方法以 Async 结尾；
- CancellationToken 尽量作为最后一个参数；
- I/O 全异步；
- 不使用 `.Result` / `.Wait()` 阻塞异步；
- 不使用 `async void`，事件 handler 除外；
- 公共 API 有 XML doc（核心协议/安全接口尤其需要）；
- 尽量 immutable record 表示协议 DTO；
- 明确长度/范围验证；
- 使用 `ConfigureAwait(false)` 可按项目风格决定，不强制 UI 层；
- 不 catch `Exception` 后什么都不做；
- catch 后要：处理、转换或记录并重新抛出；
- Dispose/Close 放 finally 或 await using。

## 3. 命名

- `LanRemote.*` namespace；
- `HostSession` / `ClientSession` 不混；
- `AccessKey` 与 `DeviceCode` 不得混用；
- `SessionToken` 与长期 secret 不得混用；
- `DeviceId` 为稳定内部 GUID；
- `DeviceCode` 为人类短码；
- `DisplayName` 为可变机器显示名。

## 4. 协议代码

所有消息：
- 有明确 DTO；
- 有 message type；
- 有 schema version；
- serializer options 固定；
- 反序列化后再验证；
- 任何 length prefix 先检查范围再分配数组；
- 禁止按远端给出的任意 int 直接 `new byte[int]`；
- payload 大数组优先 ArrayPool；
- JSON 不启用不安全多态 type handling。

## 5. 安全代码

必须：
- `RandomNumberGenerator`；
- `HMACSHA256`；
- `CryptographicOperations.FixedTimeEquals`；
- `ProtectedData`；
- `X509Certificate2` / `SslStream`；
- 证书指纹比较用原始 bytes；
- secret 生命周期尽量短。

禁止：
- `Random()` 生成密码；
- MD5/SHA1 用于安全认证；
- Base64 当加密；
- 无条件 TLS validation；
- 关闭同子网校验；
- 把 secret 输出 console/log。

## 6. 日志

推荐 Microsoft.Extensions.Logging。

日志事件应结构化：

```csharp
logger.LogInformation(
    "Session {SessionIdShort} connected from {RemoteIp} with {Permission}",
    shortId, remoteIp, permission);
```

绝不：
```csharp
logger.LogInformation("Access key: {Key}", key);
```

键盘输入内容永不记录。

## 7. UI 与线程

- ViewModel 不持有裸 Socket；
- UI 只调用 Session abstraction；
- INotifyPropertyChanged；
- collection 更新 marshal 到 Dispatcher；
- Bitmap 更新在 UI Dispatcher；
- 编码/网络不在 Dispatcher；
- 窗口 Close 时 await/session dispose 需避免死锁；
- 若 WPF closing 事件不能 await，先 cancel，异步清理后再真正关闭。

## 8. 测试规范

每修一个 bug：
- 能写回归测试就写；
- 安全 bug 必须写测试；
- protocol parser 必须有非法长度测试；
- subnet policy 必须有数据驱动测试；
- auth transcript 必须有 deterministic test vector；
- FixedTime proof 验证成功/失败均测。

## 9. Git/版本习惯（如果环境有 Git）

建议：
- `feat/discovery`
- `feat/tls-auth`
- `feat/video-stream`
- `fix/subnet-validation`

每个里程碑至少一个可构建节点。
不要一次提交几百个毫无关系文件。

如果第三方 AI 无法 commit，也必须通过 `HANDOFF.md` 记录变更。

## 10. Handoff / Handleoff 强制规范

用户提到的 “handleoff” 在本项目统一写为 `HANDOFF.md`。

### 10.1 何时写
- 每个里程碑结束；
- 每次模型准备停止；
- 每次做了架构决定；
- 每次留下已知 bug；
- 每次协议字段发生变化。

### 10.2 HANDOFF 不能写空话

错误：
- “基本完成，后续继续优化”。

正确：
- “M3 TLS listener 完成，`TlsHostListener.cs` 已通过 12 个集成测试；Video attach 尚未实现。下一步先实现 `SessionTokenRegistry`，再写 attach proof 测试。不要重写 `SubnetPolicy`，它已覆盖 /8,/12,/16,/24,/25 测试。”

### 10.3 每次 AI 回复末尾也给简短 Handoff 摘要
即使已经改文件，最终答复中也要说明：
- build；
- tests；
- next；
- blocker。

## 11. 决策记录 ADR

安全/架构决定追加到 `docs/DECISIONS.md`：
- 日期；
- Decision；
- Context；
- Consequences；
- 是否可逆。

不要每次新 AI 都推翻以前决定。
若必须推翻：
- 解释原因；
- 写新 ADR；
- 标记旧 ADR superseded。

## 12. 不允许伪造状态

绝不声称：
- “已测试”但没跑；
- “编译通过”但没 build；
- “Windows 10 验证通过”但没在 Windows 10 测；
- “安全”而没有对应机制/测试。

## 13. 依赖策略

- 优先 BCL/Windows API；
- 外部包必须：
  - 维护状态正常；
  - 许可证适合自用/分发；
  - 不是为了几行代码引入大框架；
- 固定 package version；
- 不使用随机 GitHub 二进制；
- 不下载执行未知脚本。

## 14. 错误预算

不追求 v1：
- 4K60；
- 游戏串流级延迟；
- HDR；
- DirectX 独占全屏；
- UAC/secure desktop；
- 复杂输入法同步。

先把正常桌面操作做稳定。
