# 里程碑施工顺序

> 第三方模型必须按顺序推进。前一阶段 Definition of Done 未满足，不进入下一阶段。

## M0 — 仓库骨架

任务：
- 创建 solution/projects；
- DI/Host；
- MainWindow；
- logging；
- config path；
- `HANDOFF.md`；
- 基础测试项目；
- 单实例 Mutex。

DoD：
- `dotnet build` 通过；
- app 能启动主窗口；
- logs 生成；
- 测试项目能运行。

## M1 — 设备身份与安全存储

任务：
- DeviceIdentityService；
- deviceGuid；
- deviceCode；
- access key 128-bit；
- Base32 codec；
- DPAPI secrets；
- certificate generate/load；
- fingerprint；
- “显示/复制/重新生成” UI。

测试：
- device code 稳定；
- regenerate key 后变化；
- secrets 文件不是明文；
- Base32 roundtrip；
- cert fingerprint 稳定。

DoD：
- 重启应用 identity 不变；
- access key 不出现在日志/config。

## M2 — 网卡筛选 + UDP 发现

任务：
- enumerate private IPv4 adapters；
- subnet info；
- multicast/broadcast；
- announce/probe；
- device cache；
- offline TTL；
- MainWindow device list。

测试：
- RFC1918 判定；
- mask/subnet；
- self announcement 去重；
- malformed discovery JSON 不崩。

手工：
- 两台电脑 3~6 秒互相看到。

## M3 — TLS Host/Client + 同子网校验

任务：
- TCP listeners per private NIC；
- accepted socket local/remote same subnet；
- SslStream server；
- client cert fingerprint pinning；
- channel hello；
- framing reader/writer；
- 超长消息拒绝。

测试：
- 同 /24 允许；
- 不同 /24 拒绝；
- /16 正确；
- fingerprint mismatch 拒绝；
- >1MiB control message 拒绝。

DoD：
- TLS control socket 可稳定建立；
- 没有 auth 前不能进入 Session。

## M4 — Access Key Challenge Auth

任务：
- AuthChallenge；
- canonical transcript builder；
- HMAC proof；
- server proof；
- timeout；
- failed auth limiter；
- local approval dialog；
- sessionToken；
- session registry。

测试：
- deterministic transcript；
- correct key success；
- wrong key fail；
- modified cert fingerprint fail；
- modified permission fail；
- expired challenge fail；
- 5 failures limiter；
- serverProof client validation。

DoD：
- 绝不发送 raw access key；
- auth success 后才能有 session。

## M5 — 视频最小闭环

任务：
- `IScreenCaptureBackend`；
- GDI capture primary display；
- raw frame model；
- JPEG encoder；
- video second TLS channel；
- video attach proof；
- binary frame；
- client JPEG decode/render；
- DropOldest queues。

先做到：
- 5 FPS；
- 50% scale；
- primary display。

然后：
- 10/15/20/30 FPS；
- scale；
- quality。

测试：
- video header parser；
- oversize payload reject；
- queue 不无限增长；
- disconnect dispose。

DoD：
- 持续 10 分钟没有明显内存持续增长；
- 网络慢时延迟不会不断累加。

## M6 — 多显示器 + 画质

任务：
- enumerate monitors；
- remote display metadata；
- switch display；
- scale；
- quality presets；
- ping RTT；
- actual FPS；
- auto quality 简单控制器。

DoD：
- 切换屏幕后鼠标坐标仍准确；
- 每个 Session 画质独立。

## M7 — 键鼠控制

任务：
- permission states；
- mouse normalized coordinates；
- mouse move coalescing；
- click/wheel；
- keyboard keyDown/up；
- SendInput；
- local emergency stop hotkey；
- remote-control indicator。

安全：
- view-only 永远不 inject；
- session 未认证永远不 inject；
- host AllowControl=false 永远不 inject；
- local approval 仅 View 时永远不 inject。

测试：
- permission gate；
- coordinate mapping；
- invalid coordinate clamp；
- disconnect releases stuck keys if needed。

DoD：
- 远端普通桌面可完成基本鼠标键盘操作；
- 控制关闭后输入立即失效。

## M8 — 多会话

任务：
- SessionManager；
- 多个 SessionWindow；
- 每个 session 独立 CTS/queues/connections；
- 一个 session 断开不影响其他；
- host 默认最多：
  - 3 view；
  - 1 control（可配置常量）。

测试：
- 两个 fake sessions；
- dispose isolation；
- resource cleanup。

手工：
- A 同时控制 B、C。

## M9 — 可靠性与 UX

任务：
- reconnect；
- error categories；
- tray；
- startup；
- approval timeout；
- stats overlay；
- log rotation；
- network adapter change handling。

测试：
- 拔网线/关 Wi-Fi；
- target app exit；
- host restart；
- client cancel connect；
- wrong key repeated；
- window close while streaming。

## M10 — 防火墙/发布

任务：
- self-contained x64 publish；
- configure-firewall.ps1；
- remove-firewall.ps1；
- README 用户使用说明；
- 版本号；
- release zip；
- 可选 installer。

DoD：
- 干净测试机可部署；
- 不需要开发环境；
- 防火墙只 LocalSubnet；
- uninstall/remove 脚本可撤销。

## M11 — 性能优化（可在 v1 后）

只有 M0-M10 稳定后才开始：

候选：
- Windows.Graphics.Capture；
- GPU scaling；
- H.264 hardware encoder；
- QUIC（若最低系统提高到 Windows 11+）；
- 客户端长期身份与可信设备；
- IPv6 LAN。

绝不能用 M11 的复杂度阻塞 v1。
