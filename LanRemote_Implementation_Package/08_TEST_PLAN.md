# 测试计划

## 1. 单元测试

### SubnetPolicy
至少：
- 192.168.1.10/24 vs 192.168.1.20 => true
- 192.168.1.10/24 vs 192.168.2.20 => false
- 10.1.1.1/8 vs 10.200.5.5 => true
- 172.16.1.1/12 vs 172.31.200.1 => true
- 172.16.1.1/12 vs 172.32.1.1 => false
- public IP => false
- 169.254 => false

### DeviceCode
- deterministic；
- correct format；
- no invalid chars。

### AccessKey
- exactly 128-bit entropy；
- encode/decode roundtrip；
- malformed reject；
- regenerate changes。

### AuthTranscript
建立固定 test vector：
- 固定 sessionId；
- 固定 nonces；
- 固定 cert fingerprint；
- 固定 access key；
- expected HMAC hard-code。

这样未来字段顺序变化会立刻让测试失败，防止客户端/服务端不一致。

### Protocol Framing
- 0 length；
- normal；
- partial network read；
- multiple messages in same buffer；
- > max length；
- disconnect mid-message。

### Video Header
- normal；
- unknown magic；
- wrong version；
- size 0；
- size too large；
- payload > 32MiB。

## 2. 集成测试

使用 loopback 仅测试 protocol mechanics 时允许，但 production SubnetPolicy 要可注入 fake policy。

- TLS server/client；
- cert pinning success；
- pinning mismatch；
- auth success；
- auth fail；
- video attach；
- expired token；
- control disconnect invalidates token。

## 3. 两机手工矩阵

### 网络
- 同交换机/网线；
- 同 5GHz Wi-Fi；
- 一台网线一台 Wi-Fi，若同 /24；
- Guest Wi-Fi 隔离；
- 不同 VLAN/子网；
- Windows network profile Private/Public。

期望：
- 同子网可发现/连接；
- 不同子网拒绝；
- Guest 隔离可能发现不到，文档说明；
- Public profile 若防火墙阻止，要给清晰提示。

## 4. 安全手工测试

- 错访问密钥；
- 连错设备码；
- 重置访问密钥后旧 key；
- 模拟 cert fingerprint mismatch；
- 认证时修改 requestedPermission；
- 非认证 video attach；
- 过期 attach；
- 同 IP 连续失败；
- 关闭 AllowControl 后仍发 input；
- ViewOnly 发 input；
- 超大 JSON；
- 超大 video length；
- malformed Base64。

所有情况不能崩进程。

## 5. 可靠性 soak

至少：
- 单会话持续 30 分钟；
- 有条件再测 2 小时；
- 每 5 分钟切一次画质；
- 切 display；
- 最小化/恢复；
- disconnect/reconnect 20 次。

观察：
- Working Set；
- GDI handles；
- thread count；
- socket count；
- UI responsiveness。

特别防：
- GDI handle 泄漏；
- Bitmap/MemoryStream 泄漏；
- CancellationTokenSource 泄漏；
- 未 await Task。

## 6. 网络故障

- 视频中拔网线；
- Wi-Fi off 10 秒再 on；
- host 进程 kill；
- client 进程 kill；
- video socket 单独断；
- control socket 单独断。

期望：
- control 断 => 整个 session 结束；
- video 断 => 可以尝试一次重新 attach，失败则 session 显示视频失败；
- app 不死锁；
- 其他 session 不受影响。

## 7. 性能记录

每次 release 至少记录：
- CPU host/client；
- RAM；
- 1080p 15FPS q60；
- 720p 30FPS q50；
- Mbps；
- encode ms；
- decode ms；
- RTT。

不要凭感觉声称“低延迟”。

## 8. UI 验收

- 设备码复制；
- 密钥显示/隐藏；
- 重新生成有确认；
- connect dialog 可取消；
- approval timeout；
- fullscreen 退出；
- letterbox 坐标；
- 多窗口；
- 一键断开；
- tray 显示被控状态。

## 9. 发布前安全检查

搜索仓库：
- `return true` 的 cert validation；
- `AccessKey` logging；
- 固定测试密码；
- `TODO: disable auth`；
- `SslProtocols.Tls` 旧版本；
- `Random()` 密码；
- 任意 `remoteip=Any` 防火墙；
- UPnP/NAT 代码。

发现即阻止发布。
