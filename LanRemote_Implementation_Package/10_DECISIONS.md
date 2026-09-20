# Architecture Decision Records

## ADR-001 — Windows-first
**Decision**：v1 只做 Windows 10 22H2 / Windows 11 x64。  
**Context**：远程屏幕捕获和输入控制高度依赖 OS API。跨平台会显著增加第三方 AI 施工失败概率。  
**Consequence**：接口保留跨平台可能，但不在 v1 实现。

## ADR-002 — .NET 10 LTS + WPF
**Decision**：使用 .NET 10 LTS 和 WPF。  
**Context**：成熟 Windows 桌面生态、SslStream、DPAPI、Win32 interop、异步网络能力充足。  
**Consequence**：UI 非跨平台。

## ADR-003 — v1 使用 TLS/TCP，而不是 QUIC
**Decision**：control/video 各用独立 TCP+TLS。  
**Context**：需要兼容 Windows 10；.NET QUIC 在 Windows 的平台要求更偏向 Windows 11/Server 2022+，而 TCP/TLS 可覆盖目标系统。两条连接能显著降低视频写入对输入控制的阻塞。  
**Consequence**：弱 Wi-Fi 丢包时视频延迟可能不如专用 UDP/QUIC，但 v1 更可靠易实现。

## ADR-004 — Access key 为 128-bit 随机 key
**Decision**：系统生成高熵 key，而不是 6 位密码。  
**Context**：认证 proof 可被网络攻击者观察；若密码弱会有离线字典风险。  
**Consequence**：密钥较长，但安全；UI 用分组和复制提高易用性。

## ADR-005 — HMAC challenge，不发送明文 key
**Decision**：TLS 内仍不直接发送访问 key，而发送绑定 transcript 的 HMAC proof，并验证 server proof。  
**Context**：局域网发现可被伪造；这样恶意伪造服务端拿不到 key 本身。  
**Consequence**：协议稍复杂，但仍只使用标准原语。

## ADR-006 — 同局域网定义为同 IPv4 子网
**Decision**：v1 仅接受与 listener 本地地址相同 subnet 的 RFC1918 IPv4。  
**Context**：“同一个路由器”无法可靠由应用判断；同 subnet 可明确验证。  
**Consequence**：跨 VLAN、路由型企业 LAN 默认不可连接，这是安全设计。

## ADR-007 — 视频先 JPEG/MJPEG
**Decision**：v1 先完成 JPEG frame streaming，编码器抽象可替换。  
**Context**：H.264 硬件编码/Media Foundation 增加大量 COM/GPU/驱动复杂度，会阻塞主体功能。LAN 下通过缩放/FPS/quality 可先达到自用可用性。  
**Consequence**：带宽高于 H.264；后续性能阶段可升级。

## ADR-008 — GDI capture 首发
**Decision**：v1 可用 BitBlt，后续 Windows.Graphics.Capture。  
**Context**：BitBlt 更容易一次施工正确。  
**Consequence**：高 FPS/4K 性能不是 v1 目标；secure desktop 不支持。

## ADR-009 — 无隐身远控
**Decision**：被查看/控制必须有本机指示和紧急断开。  
**Context**：减少误连接和隐私风险。  
**Consequence**：不提供 stealth 功能。
