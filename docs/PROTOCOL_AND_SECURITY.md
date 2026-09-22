# 协议与安全规格

> **本地修订记录**。本副本是规格包 `LanRemote_Implementation_Package/04_PROTOCOL_AND_SECURITY.md`
> 的本地工作副本（规格包只读、不回写）。随里程碑落地同步修订；**与原文冲突时以
> `docs/DECISIONS.md` 的 ADR 为准**。修订点以 `【本地修订 N】` 标记，原文保留以便对照。
>
> - 【本地修订 1】2026-09-21，依据 ADR-038 第 1 条（§9 认证）：transcript 拆为
>   ClientAuthTranscript / ServerGrantTranscript **双档**；`serverProof` 绑定 grant 档
>   （原文的单 transcript 双 proof 形态作废）。

## 1. 威胁模型

考虑：
- 同一 Wi-Fi 中有陌生设备；
- 有人伪造发现包；
- 有人尝试爆破；
- 有人尝试跨子网连接；
- 有人监听局域网；
- 非预期设备误点连接；
- 日志泄密；
- 巨大 payload 导致内存占用；
- 恶意/损坏消息触发解析崩溃。

不声称防御：
- 被控电脑本身已被管理员/恶意软件完全控制；
- Windows 内核被攻破；
- 用户主动把访问密钥泄露；
- 同一用户账号的本机恶意程序读取用户内存。

## 2. 端口

- UDP discovery: `45872`
- TCP TLS: `45873`

未来允许改端口，但协议 v1 固定，减少复杂度。

## 3. 合法网卡

仅 IPv4。

默认接受：
- Ethernet；
- Wireless80211；
- OperationalStatus.Up；
- 有 IPv4 + IPv4 mask；
- IPv4 属于 RFC1918：
  - 10.0.0.0/8
  - 172.16.0.0/12
  - 192.168.0.0/16

默认拒绝：
- Loopback；
- Tunnel；
- 公网 IPv4；
- 169.254.0.0/16；
- 没有 subnet mask 的地址；
- VPN/虚拟网卡（v1 可按类型/描述过滤，必要时在设置中提供实验性开关）。

## 4. 同子网校验

对 accepted TCP：

- `local = socket.LocalEndPoint.Address`
- `remote = socket.RemoteEndPoint.Address`
- 找到拥有 `local` 的 NIC 和 mask；
- 计算：
  - `localNetwork = local & mask`
  - `remoteNetwork = remote & mask`
- 必须相等；
- `remote` 必须是 RFC1918；
- 否则立即关闭。

不要仅判断“都是 192.168.x.x”；必须按真实 mask。

## 5. Discovery

建议：
- IPv4 multicast：`239.255.77.77:45872`
- TTL=1；
- 同时可向每个网卡 directed broadcast 发送 discovery probe，提高兼容性；
- 不向公网网卡发；
- 每 2 秒 announce；
- 设备 cache TTL 7 秒。

### 5.1 DiscoveryAnnouncement JSON

长度控制在 2 KB 以内。

```json
{
  "magic": "LANREMOTE",
  "protocol": 1,
  "type": "announce",
  "deviceId": "d9e1...",
  "deviceCode": "7K3M-P9QX",
  "deviceName": "DESKTOP-A",
  "appVersion": "1.0.0",
  "tcpPort": 45873,
  "certSha256": "HEX...",
  "capabilities": ["view", "control", "multi-monitor"],
  "nonce": "base64 random 12 bytes"
}
```

发现包不是认证依据。
唯一可用于 TLS pinning 的 `certSha256` 仍需后续 serverProof 验证访问密钥持有者。

### 5.2 Probe

```json
{
  "magic": "LANREMOTE",
  "protocol": 1,
  "type": "probe",
  "nonce": "..."
}
```

收到 probe 立即 announce 一次。

## 6. TCP framing

Control 使用 length-prefixed UTF-8 JSON：

```text
[4 bytes big-endian uint32 length][length bytes UTF-8 JSON]
```

限制：
- Control message 最大 1 MiB；
- 正常消息应远小于 64 KiB；
- 超限立即断开并记录安全日志。

Video 使用 binary header + JPEG payload。

## 7. TLS

每台设备生成 Host identity certificate：

- ECDSA P-256；
- self-signed；
- 有效期可 5 年；
- Subject 只放本地软件标识，不放敏感信息；
- SHA-256 fingerprint；
- 私钥 DPAPI 保护。

客户端：
- 从 discovery 得到 expected fingerprint；
- TLS validation callback 中只接受 fingerprint 完全相等；
- 使用 `CryptographicOperations.FixedTimeEquals` 比较 byte fingerprint；
- 不允许 `return true` 无条件放过证书。

协议协商：
- TLS 1.2 / TLS 1.3；
- 不允许 SSL3/TLS1.0/TLS1.1。

## 8. Access Key

生成：
```csharp
byte[] key = RandomNumberGenerator.GetBytes(16); // 128-bit
```

显示：
- Crockford/Base32；
- 约 26 字符；
- 分组便于输入；
- 输入解析时忽略空格和 `-`；
- 不区分大小写（如果所选 Base32 方案如此定义）。

例：
`K7M2P-9D4TW-8XQ3N-6R5CV-ZA`

不要把这个示例当固定 key。

存储：
- DPAPI CurrentUser；
- secrets 文件权限保持当前用户；
- UI 默认 mask；
- copy 后可以在 30 秒后尝试清空剪贴板，但只有剪贴板仍等于原值时才清，避免覆盖用户新内容。

## 9. Auth Protocol

### 9.1 Control Channel Hello

TLS 后 Client 先发：

```json
{
  "type": "channel_hello",
  "channel": "control",
  "protocol": 1
}
```

Server 响应 Challenge：

```json
{
  "type": "auth_challenge",
  "protocol": 1,
  "sessionId": "uuid",
  "serverDeviceId": "...",
  "serverNonce": "base64 32 bytes",
  "certSha256": "HEX...",
  "expiresInMs": 15000
}
```

Client 生成 32-byte `clientNonce`。

Canonical transcript 必须用**固定字段顺序的二进制/UTF8 构造函数**，不要直接对任意 JSON 字符串做 HMAC，因为空格/字段顺序会不同。

> 【本地修订 1】以下**双档** transcript 为定案形态（ADR-038 第 1 条）；字节级实现见
> `src/LanRemote.Security/Auth/AuthTranscriptBuilder.cs`，独立黄金向量见
> `scripts/reference/gen-auth-golden-vectors.py`。

```text
ClientAuthTranscript =
  LANREMOTE-AUTH-V1\0
  sessionId\0
  serverDeviceId\0
  clientDeviceId\0
  serverNonce(base64 canonical)\0
  clientNonce(base64 canonical)\0
  certSha256(uppercase hex)\0
  requestedPermission(view|control)          ← 末尾字段，无尾随 \0
```

Client：
```text
clientProof = HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)
```

发送：

```json
{
  "type": "auth_response",
  "clientDeviceId": "...",
  "clientName": "DESKTOP-B",
  "clientNonce": "...",
  "requestedPermission": "control",
  "clientProof": "base64"
}
```

Server：
- challenge 未过期；
- device/session 对得上；
- proof base64 长度正确；
- 重算；
- FixedTimeEquals；
- 失败统一返回 generic `authentication_failed`；
- 不区分“密码错/设备码错”等细节给远端。

成功后如需本机审批：
```json
{
  "type": "approval_pending"
}
```

本机同意后：

```text
ServerGrantTranscript =
  LANREMOTE-GRANT-V1\0
  SHA256(ClientAuthTranscript 的 UTF-8 字节)的 uppercase hex\0
  grantedPermission

serverProof = HMAC-SHA256(accessKeyBytes, "server\0" || ServerGrantTranscript)
sessionToken = random 32 bytes
```

> 【本地修订 1】`serverProof` 由「绑 `transcript`」改为「绑 ServerGrantTranscript」：
> `grantedPermission` 只进 grant 档；客户端验证前用**自己那份** transcript 与收到的 granted
> 重建 grant 档——**granted 被中途篡改即验证失败**（ADR-038 第 1 条）。

发送：
```json
{
  "type": "auth_success",
  "grantedPermission": "control",
  "serverProof": "base64",
  "sessionToken": "base64",
  "videoAttachExpiresInMs": 15000
}
```

Client 必须验证 serverProof。
验证失败：
- 立即断开；
- UI 显示“远端身份验证失败，可能是错误密码或伪造设备广播”；
- 不发送输入。

## 10. Video Attach

Client 新建第二条 TLS 连接，仍做相同 cert pinning。

发 hello：
```json
{
  "type": "channel_hello",
  "channel": "video",
  "protocol": 1,
  "sessionId": "...",
  "attachNonce": "base64 16 bytes",
  "attachProof": "base64"
}
```

其中：
```text
attachProof = HMAC-SHA256(
  sessionToken,
  UTF8("LANREMOTE-VIDEO-V1\0") ||
  sessionId ||
  attachNonce ||
  certSha256
)
```

Server 验证后把 socket 绑定到已认证 session。

sessionToken：
- 只存在内存；
- session 断开立即废弃；
- 不写日志；
- 不持久化。

## 11. Control Message Types

至少：

- `ping`
- `pong`
- `request_quality`
- `quality_applied`
- `request_display`
- `display_changed`
- `input_mouse_move`
- `input_mouse_button`
- `input_mouse_wheel`
- `input_key`
- `permission_changed`
- `disconnect`
- `error`
- `host_status`

每个消息有：
```json
{
  "type": "...",
  "seq": 123,
  "timestampMs": 123456789
}
```

## 12. 输入消息

坐标一律 normalized，不直接发客户端像素。

```json
{
  "type": "input_mouse_move",
  "x": 0.0,
  "y": 1.0
}
```

范围：
- x/y: `[0, 1]`
- server clamp；
- 映射到当前 remote display rectangle。

键盘：
- 传 Windows virtual key + scan code + flags；
- 明确 keyDown/keyUp；
- server 限流；
- 不允许构造 SAS/Ctrl+Alt+Del；
- `SendInput` 本身无法突破 Windows UIPI，保持普通权限。

## 13. Video Frame Format

建议固定二进制 header：

```text
4  bytes magic = "LRVF"
1  byte  version = 1
1  byte  codec = 1 (JPEG)
2  bytes flags
8  bytes frameId (uint64 BE)
8  bytes timestampUs (uint64 BE)
4  bytes width (uint32 BE)
4  bytes height (uint32 BE)
1  byte  jpegQuality
3  bytes reserved
4  bytes payloadLength (uint32 BE)
N  bytes JPEG payload
```

限制：
- width <= 8192
- height <= 8192
- payloadLength <= 32 MiB
- 任意字段非法：结束 video channel，不让 parser 继续错位读取。

## 14. 防爆破

虽然 128-bit key 已足够强，仍必须限流：

按 remote IP：
- 10 分钟窗口；
- 连续 5 次失败后暂时拒绝 60 秒；
- 每次失败加入小幅延时，如 300~800ms 随机；
- 成功后可清失败计数；
- 不把 key/proof 打日志。

本机 UI 可显示：
“来自 192.168.1.50 的认证失败 5 次”。

## 15. 本地审批

默认 `RequireLocalApprovalForUnknownController=true`。

v1 可简单定义“unknown”=每次都是 unknown，始终审批；或者只在当前运行期记住已同意的 clientDeviceId。

若以后做持久信任设备：
- 不能只信 deviceId 字符串；
- 必须加入客户端持有的长期公钥/证书，并做密钥所有权证明；
- 该功能放 v1.1，不要草率实现。

## 16. 防火墙

最终安装阶段创建两条 inbound rule：

- LanRemote Discovery UDP 45872；
- LanRemote TLS TCP 45873；

要求：
- program = 安装后的 LanRemote.exe；
- remoteip = LocalSubnet；
- profile 尽量 Private；
- 不开 Any remote address；
- 不创建 outbound 互联网例外；
- 不做端口转发。

提供：
`scripts/configure-firewall.ps1`
以及可撤销脚本。

## 17. 安全日志

可记录：
- 时间；
- remote IP；
- device code；
- session id 的短前缀；
- 状态；
- auth success/fail；
- disconnect reason。

绝不记录：
- accessKey；
- raw secret；
- clientProof/serverProof；
- sessionToken；
- certificate private key；
- 屏幕帧；
- 键盘实际文本内容。

键盘事件日志默认完全关闭。
