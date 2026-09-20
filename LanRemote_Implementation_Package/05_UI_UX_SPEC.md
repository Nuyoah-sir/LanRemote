# UI / UX 规格

## 1. MainWindow

推荐布局：

```text
┌──────────────────────────────────────────────────────┐
│ LanRemote                                            │
├──────────────────────────────────────────────────────┤
│ 本机                                                 │
│ 名称: DESKTOP-A                                      │
│ 设备码: 7K3M-P9QX   [复制]                           │
│ 访问密钥: •••••••••••••••••••• [显示] [复制] [重置] │
│ [✓] 允许被发现  [✓] 允许查看  [✓] 允许控制           │
│ [✓] 陌生控制端首次连接需确认                         │
│ [ ] 登录 Windows 后自动启动                          │
├──────────────────────────────────────────────────────┤
│ 局域网设备                         [刷新]             │
│ ● DESKTOP-B   A1B2-C3D4  192.168.1.20   [连接]      │
│ ● OFFICE-PC   P9Q8-X7W6  192.168.1.30   [连接]      │
└──────────────────────────────────────────────────────┘
```

设备列表状态：
- Online；
- Connecting；
- Auth required；
- Connected；
- Offline；
- Blocked（非同子网理论上不应进入列表）。

## 2. Connect Dialog

显示：
- 远端名称；
- 设备码；
- IP；
- 访问密钥输入；
- 权限：
  - 只查看；
  - 查看并控制；
- “连接”。

提示：
“访问密钥只用于本次连接，不会通过网络明文发送。”

v1 默认不提供“永久记住密码”。

## 3. Host Approval Dialog

被控端弹出：

```text
DESKTOP-B (192.168.1.20)
正在请求“查看并控制”此电脑。

[拒绝] [仅允许查看] [允许控制]
```

超时：
- 30 秒无操作默认拒绝。

当主机设置“首次连接需确认”开启时必须弹。

## 4. SessionWindow

顶部浮动/固定 toolbar：

- 设备名；
- 状态；
- View / Control；
- Monitor 下拉；
- Quality preset；
- FPS；
- Scale；
- Fullscreen；
- Stats；
- Disconnect。

主区域：
- 黑色背景；
- 保持远端屏幕比例；
- 需要 letterbox；
- 鼠标坐标映射必须只针对实际图像 rectangle，不包含黑边。

## 5. 输入焦点

- 会话窗口获得焦点且处于 Control 模式才捕获键盘；
- ViewOnly 不发送任何输入；
- 断开/认证中不发送；
- Alt+Tab 等本地系统快捷键要谨慎，v1 不做全键盘 hook；
- 普通 WPF KeyDown/KeyUp 能收到的才发送；
- 全屏可增加“释放键盘”按钮；
- `Esc` 可退出全屏，但不发送到远端时需有明确规则。

## 6. Pointer Mapping

远端画面渲染 rect：
- `imageX, imageY, imageWidth, imageHeight`

本地 pointer：
- 如果在黑边外，不发送 move；
- normalized:
  - `(px-imageX)/imageWidth`
  - `(py-imageY)/imageHeight`
- clamp 到 0~1。

Server 再映射到目标 monitor desktop coordinates。

## 7. Stats Overlay

可选显示：
- Remote IP；
- RTT；
- actual FPS；
- encoded width x height；
- JPEG quality；
- receive Mbps；
- dropped frames；
- decode time。

不能显示 access key/session token。

## 8. 错误文案

必须区分可理解类别：
- 未发现设备；
- 目标已离线；
- 同子网校验失败；
- 防火墙阻止；
- TLS 指纹不匹配；
- 认证失败；
- 本机拒绝；
- 只允许查看；
- 视频通道连接失败；
- 捕获失败；
- 网络断开。

不要把原始 stack trace 直接弹给用户；stack trace 写日志。

## 9. 安全可见性

被控时：
- tray icon 明显变化；
- MainWindow 顶部显示“正在被查看/控制”；
- 一键断开；
- 全局停止热键；
- 不能做“完全隐身控制模式”。

这条是产品安全要求，不允许删除。
