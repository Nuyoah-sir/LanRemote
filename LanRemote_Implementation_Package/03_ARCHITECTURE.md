# 架构设计

## 1. 总体图

```text
┌──────────────────────── LanRemote.App ────────────────────────┐
│ MainWindow / SessionWindow / Tray / ViewModels               │
└─────────────┬──────────────────────┬──────────────────────────┘
              │                      │
      ┌───────▼────────┐    ┌────────▼─────────┐
      │ Discovery      │    │ Session Manager  │
      │ UDP LAN scan   │    │ host + clients  │
      └───────┬────────┘    └───────┬──────────┘
              │                     │
      ┌───────▼────────┐      ┌─────▼─────────────────────┐
      │ Subnet Policy  │      │ TLS Transport            │
      │ NIC filtering  │      │ Control + Video sockets │
      └────────────────┘      └─────┬──────────┬──────────┘
                                    │          │
                           ┌────────▼───┐  ┌───▼──────────┐
                           │ Capture    │  │ Remote Input │
                           │ + Encoder  │  │ SendInput    │
                           └────────────┘  └──────────────┘
```

## 2. 进程模型

- 一个 `LanRemote.exe`；
- 单实例主进程（Mutex）；
- 多个 `SessionWindow`；
- 不为每个远端启动一个新进程；
- 后台 Host listener 和 Discovery 在主进程中运行；
- 用户关闭主窗口时可选择最小化到托盘而不是退出；
- “退出”才真正停止网络监听。

这样符合“多开窗口连接多台设备”，同时状态和资源容易管理。

## 3. 网络线程模型

每个服务用独立异步循环，不创建无上限线程。

### Discovery
- 每个合法 NIC 建 socket；
- 接收 loop；
- announce loop；
- device cache cleanup loop。

### Host
- 每个合法 NIC 建 TCP listener；
- accept loop；
- 每个 accepted connection 单独 Task；
- connection 首先执行：
  1. endpoint 校验；
  2. TLS；
  3. channel hello；
  4. control auth 或 video attach。

### Client
每个 Session：
- control connection；
- video connection；
- ping loop；
- input outbound queue；
- video read loop；
- decode/display loop。

## 4. 为什么 v1 使用“两条 TLS/TCP”

一条 TCP 同时塞 JPEG 视频和键鼠会产生应用层排队：
- 大 JPEG 包正在写入时，控制小包可能等待；
- 网络抖动时远程控制体感变差。

因此：
- Control TCP：小消息、低延迟；
- Video TCP：大数据；
- 两条连接互相独立；
- 两条都 TLS；
- Video 必须用认证后拿到的 sessionToken 附着到同一会话。

## 5. 视频流水线

```text
Capture Timer
    │
    ▼
Capture raw frame
    │
    ▼
Bounded Channel<CapturedFrame> capacity=2 DropOldest
    │
    ▼
Scale + JPEG encode
    │
    ▼
Bounded Channel<EncodedFrame> capacity=2 DropOldest
    │
    ▼
TLS Video Writer
```

关键原则：
- 实时远控宁可丢旧帧，不可积压；
- capture loop 不等待网络；
- encoder 不在 UI thread；
- encoded queue 满时丢最旧；
- 客户端 decode/render 也只保留最新 1~2 帧。

## 6. Capture 后端

接口：
`IScreenCaptureBackend`

### v1 后端：GDI BitBlt
优点：
- 实现简单；
- Windows 10/11 普通桌面兼容；
- 适合先完成可靠产品。

要求：
- 使用 `GetDC` / compatible DC / DIBSection / `BitBlt`；
- 所有 GDI handle 必须 `finally` 释放；
- 不允许每帧泄漏 HBITMAP/HDC；
- 用 ArrayPool/MemoryPool 减少大数组分配；
- 处理多显示器负坐标；
- 捕获失败可重试。

限制：
- UAC 安全桌面/锁屏不保证；
- 高分辨率高 FPS CPU 可能高。

### 后续后端：Windows.Graphics.Capture
只能作为独立优化里程碑，不得破坏 v1 接口。

## 7. 编码器

接口：
`IFrameEncoder`

v1：
- JPEG；
- 支持缩放；
- Quality 40~85；
- 输出 `EncodedFrame(codec=Jpeg, width, height, frameId, timestamp, bytes)`。

实现可用：
- WPF imaging/WIC；
- 或一个维护良好、许可证明确的本地 JPEG 库。

禁止为了方便引入需要上传到云端的编码服务。

## 8. 客户端显示

- video read loop 读 frame header + payload；
- 验证 payload length 上限；
- JPEG decode；
- 只把最新 frame marshal 到 UI；
- UI 使用 `WriteableBitmap` / `BitmapImage` 等；
- Bitmap stream 生命周期正确；
- 不在 UI 上做网络读取。

## 9. 输入路径

```text
SessionWindow mouse/keyboard
       │
       ▼
normalize coordinates / coalesce mouse move
       │
       ▼
Control TLS JSON message
       │
       ▼
Host permission gate
       │
       ▼
map normalized coordinates to target display
       │
       ▼
SendInput
```

鼠标移动建议：
- UI 原始 MouseMove 不要每个事件都发；
- 采样或 coalesce 到约 60~120 events/s；
- 只保留最新 pointer position；
- Click/Wheel/Key 不可随意丢。

## 10. 断线与重连

默认：
- 连接断开后 SessionWindow 显示 Disconnected；
- 自动重连可提供开关；
- 若自动重连，指数退避：1s, 2s, 4s, 8s，最大 10s；
- 重连仍必须重新认证；
- 不缓存明文访问密钥到磁盘；
- 当前会话内可把访问密钥安全地放在内存中，窗口关闭立即释放引用；
- 如 UI 提供“记住密钥”，必须另行设计 DPAPI 按远端设备保存；v1 建议不做。

## 11. 资源生命周期

Session 必须是 `IAsyncDisposable`：
- cancel CTS；
- 停止 ping/input/video loops；
- 关闭 streams；
- 关闭 sockets；
- 清空队列；
- 释放 bitmap/encoder/capture 资源；
- 通知 UI 最终状态。

任何 Session 关闭都不能终止全局 Host 或其他 Sessions。
