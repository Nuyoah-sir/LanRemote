# 产品规格 PRD

## 1. 用户故事

### US-01 本机身份
作为用户，我启动软件后可以看到本机名称、设备码和访问密钥，以便在另一台电脑确认我要连接的是哪台设备。

### US-02 自动发现
两台电脑处于同一 IPv4 子网并启动 LanRemote 后，主窗口能自动显示彼此，无需输入 IP。

### US-03 安全连接
点击某台设备后，必须输入该设备访问密钥。认证失败不能看到屏幕、不能控制。

### US-04 多设备
控制端可同时连接多台设备，每个会话为独立窗口。

### US-05 画质调节
每个会话独立调整帧率、缩放、JPEG 质量，不影响其他会话。

### US-06 只看 / 控制
用户可以建立只看会话，或申请控制会话。被控机可以关闭控制能力但仍允许查看。

### US-07 本机紧急停止
被控机本地用户可立即停止当前远程控制。

## 2. 非目标

v1 明确不做：

- 公网；
- 手机端；
- 浏览器端；
- macOS/Linux；
- 云同步；
- 用户账号；
- 中继；
- NAT 穿透；
- UPnP；
- 音频；
- 文件；
- 剪贴板；
- 摄像头；
- 多人协作标注；
- 录屏；
- Windows 登录界面；
- UAC 安全桌面；
- Ctrl+Alt+Del；
- 提权注入。

## 3. 主状态

### Host 状态
- Disabled
- Discoverable
- Ready
- Authenticating
- AwaitingLocalApproval
- Viewing
- Controlled
- Error

### Client Session 状态
- Discovered
- Connecting
- Authenticating
- AwaitingApproval
- AttachingVideo
- ConnectedViewOnly
- ConnectedControl
- Reconnecting
- Disconnected
- Failed

状态变化必须可观测，UI 不能只用一个 `IsConnected` Boolean 表示所有情况。

## 4. 多显示器

v1 必须：
- 能列出远端显示器；
- 默认主显示器；
- 会话中可切换显示器；
- 切换后更新画面尺寸和鼠标坐标映射。

不要求：
- 一次把多个显示器拼成一张超宽画面。

## 5. 控制权限

主机配置：

- `AllowDiscovery`：默认 true；
- `AllowViewing`：默认 true；
- `AllowControl`：默认 true；
- `RequireLocalApprovalForUnknownController`：默认 true；
- `AutoStartOnLogin`：可选，建议 true；
- `ShowRemoteControlIndicator`：必须 true，不能关闭。

当存在控制会话时：
- 托盘图标/主窗口明显显示“正在被控制”；
- 本机提供“立即断开所有控制”；
- 建议全局热键：`Ctrl + Alt + Shift + Esc` 停止所有控制会话。

## 6. 性能目标（不是绝对硬件保证）

v1 设计目标：

- 同网线千兆 LAN：
  - 1080p，15~30 FPS，可用；
  - 单会话控制输入体感及时；
- 普通 5GHz Wi-Fi：
  - 720p/900p，15~30 FPS，可用；
- 多会话：
  - 至少 2 个 720p/15 FPS 会话互相独立。

性能不达标时优先：
1. 降低缩放；
2. 降低 JPEG 质量；
3. 降低 FPS；
4. 再考虑硬件 H.264 后端。

## 7. 质量预设

建议：

| 模式 | 缩放 | FPS | JPEG |
|---|---:|---:|---:|
| 低延迟 | 50~67% | 30 | 45 |
| 均衡 | 75% | 20~30 | 60 |
| 高画质 | 100% | 15~30 | 75 |
| 自动 | 动态 | 动态 | 动态 |

手动范围：
- FPS：5/10/15/20/30；
- 可以预留 45/60，但若编码/CPU 不达标可在 v1 UI 标记实验性；
- 缩放：50/67/75/100；
- JPEG：40~85。

## 8. 自动质量逻辑

v1 自动模式不需要复杂 AI/网络模型。

维护：
- control RTT；
- 最近 3 秒视频写入耗时；
- encoder 平均耗时；
- send queue 是否持续满；
- 实际接收 FPS。

降级条件示例：
- 视频发送队列连续 500ms 有积压；
- 或平均发送耗时 > 目标帧间隔 1.5 倍；
- 或编码耗时 > 目标帧间隔 80%。

降级顺序：
1. JPEG -10；
2. 缩放一级；
3. FPS 一级。

恢复条件：
- 稳定 5~10 秒；
- 队列不满；
- 编码/发送耗时明显低于预算。

恢复顺序反向，但每次只升一级，避免抖动。

## 9. 配置与数据目录

建议：
- `%LOCALAPPDATA%\LanRemote\config.json`
- `%LOCALAPPDATA%\LanRemote\secrets.bin`
- `%LOCALAPPDATA%\LanRemote\logs\`

`config.json` 可以包含普通设置；
`secrets.bin` 保存 DPAPI 保护的：
- access key；
- TLS certificate PFX/private key；
- 其他未来秘密。

禁止把 secrets 写进 config.json。
