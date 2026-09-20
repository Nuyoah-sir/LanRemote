# LanRemote — 第三方 AI 单文件完整施工 Prompt
> 将本文件完整交给施工模型。模型必须从头读完，并按里程碑执行。


---

<!-- SOURCE: 00_README.md -->

# LanRemote 局域网屏幕共享与远程控制 — 施工规格包

> 目标：做一个**仅供自己使用、无需账号/云端、仅允许同一局域网/同一 IPv4 子网设备发现和连接**的 Windows 桌面软件。
>
> 本规格把“能跑起来”和“安全边界”放在第一位。第三方 AI/开发者必须按里程碑推进，不得为了省事删除认证、同子网校验、TLS、秘密保护、输入权限控制或 HANDOFF 机制。

## 0. 默认前提

- 第一阶段平台：Windows 10 22H2 / Windows 11，x64。
- 技术栈：C# + .NET 10 LTS + WPF。
- UI：单进程，多会话窗口。
- 网络：仅 IPv4；局域网发现 UDP；连接使用 TCP + TLS。
- 视频 v1：JPEG/MJPEG 帧流，分辨率/FPS/JPEG 质量可调；架构必须可替换编码器。
- 输入：Win32 `SendInput`，普通用户权限运行。
- 不做：账号、云服务器、公网控制、NAT 穿透、UPnP、端口映射、第三方中继、剪贴板同步、文件传输、音频、Ctrl+Alt+Del、Windows 锁屏/UAC 安全桌面控制。
- 安全目标：只有同 IPv4 子网设备才允许进入连接流程；连接仍必须通过访问密钥认证；访问密钥绝不能以明文通过网络发送。

## 1. 文件阅读顺序

第三方模型开始施工前，必须依次读取：

1. `01_MASTER_PROMPT.md`
2. `02_PRODUCT_SPEC.md`
3. `03_ARCHITECTURE.md`
4. `04_PROTOCOL_AND_SECURITY.md`
5. `05_UI_UX_SPEC.md`
6. `06_DEV_STANDARDS.md`
7. `07_MILESTONES_AND_TASKS.md`
8. `08_TEST_PLAN.md`
9. `09_HANDOFF_TEMPLATE.md`
10. `10_DECISIONS.md`
11. `11_ACCEPTANCE_CHECKLIST.md`
12. `AGENTS.md`

如果只能给第三方模型一个文件，则使用 `MASTER_PROMPT_FULL.md`。

## 2. 关键产品事实

### 2.1 “已安装设备扫描”的真实含义

软件不能扫描“某台机器硬盘上是否安装了软件”，只能发现**当前网络中正在运行 LanRemote 后台服务/托盘进程并主动广播自己的设备**。

因此：
- 默认支持“开机登录后自动启动到托盘”；
- 只有进程正在运行、网络允许广播/组播、并处于同一 IPv4 子网，才会出现在设备列表；
- Guest Wi-Fi/AP isolation/VLAN 隔离会导致发现失败，这是正常且符合安全目标的行为。

### 2.2 为什么局域网仍然需要帧率/分辨率调节

仍然需要。影响因素包括：
- Wi-Fi 信号与干扰；
- 2.4 GHz / 5 GHz / 6 GHz 差异；
- 多台设备同时连接；
- 4K 屏幕带宽；
- JPEG 编码 CPU；
- 被控机和控制机解码/绘制能力。

所以必须提供：
- 自动；
- 低延迟；
- 均衡；
- 高画质；
- 手动 FPS/缩放/JPEG 质量。

### 2.3 同局域网的安全边界

v1 默认定义“允许连接”为同时满足：

1. 远端 IPv4 与服务端被连接网卡 IPv4 位于同一子网；
2. 只监听私有 IPv4 网卡；
3. UDP 发现只在私有网卡上进行；
4. 不启用任何公网发现/中继/NAT 穿透/UPnP；
5. Windows 防火墙规则限制为 LocalSubnet；
6. TLS 加密；
7. 访问密钥挑战认证成功；
8. 如开启“陌生设备首次需本机确认”，还需本机人工同意。

“连接同一个物理路由器”无法被应用程序直接证明。这里采用“同 IPv4 子网”作为默认的、清晰可执行的安全定义。

## 3. v1 成功标准

- 两台 Windows 电脑同子网，均启动软件后 3~6 秒内互相发现。
- 输入正确访问密钥后可打开独立控制窗口。
- 可同时连接至少 2 台远端设备，每台一个独立窗口。
- 默认情况下不接受跨子网 IPv4。
- 所有控制和视频内容经过 TLS。
- 访问密钥不以明文上网/落日志。
- 可选择只看 / 控制。
- 支持鼠标移动、点击、滚轮、常用键盘按键。
- 支持 5/10/15/20/30 FPS；缩放 50/67/75/100%；JPEG 质量至少 40~85。
- 网络/编码堵塞时不能形成“越积越延迟”的长队列；旧视频帧必须可丢弃。
- 断网后 UI 不死锁，可重连。
- 每次开发结束都更新 `HANDOFF.md`。

---

<!-- SOURCE: 01_MASTER_PROMPT.md -->

# 给施工 AI 的主 Prompt

你现在是这个项目唯一的资深 Windows 桌面、网络、安全与实时图像传输工程师。项目名暂定 **LanRemote**。你的任务不是写演示代码，而是持续把仓库施工成可运行、可测试、可维护的自用软件。

## 一、不可改变的产品目标

制作一个 Windows 局域网远程桌面工具：

- 同一局域网/同一 IPv4 子网中的设备可自动发现；
- 每个设备显示稳定设备码；
- 每个设备有独立的安全访问密钥；
- 无账号、无登录、无云服务器；
- 一个控制端进程可以打开多个独立会话窗口，同时连接多台电脑；
- 每个会话可调帧率、缩放比例、JPEG 质量；
- 支持查看屏幕与键鼠控制；
- 默认只允许同 IPv4 子网；
- 不做公网控制、NAT 穿透、UPnP、端口映射、第三方中继；
- 默认安全优先，不能为了“方便测试”永久关闭安全校验。

## 二、默认技术栈

- C#；
- .NET 10 LTS；
- WPF；
- x64；
- `net10.0-windows10.0.19041.0` 或等价可支持目标系统的 Windows TFM；
- `Nullable=enable`；
- `ImplicitUsings=enable`；
- 异步 I/O 使用 `async/await`；
- 网络控制通道和视频通道使用两个独立 TLS/TCP 连接，避免视频大包阻塞键鼠控制；
- UDP 局域网发现；
- v1 视频编码为 JPEG/MJPEG；
- 屏幕采集 v1 允许先使用 Win32 GDI/BitBlt 后端，但必须通过接口隔离，以便后续替换 Windows.Graphics.Capture；
- 输入注入通过 Win32 `SendInput`；
- 设备密钥/访问密钥使用 Windows DPAPI (`ProtectedData`, CurrentUser) 存储；
- 日志不得记录访问密钥、挑战 proof、session token、证书私钥或完整屏幕内容。

## 三、必须遵守的工程方式

### 3.1 不允许“大爆炸式一次写完”

严格按 `07_MILESTONES_AND_TASKS.md` 的阶段推进。每个里程碑必须：

1. 编译通过；
2. 对应单元/集成测试通过；
3. 手工验证关键路径；
4. 更新 `HANDOFF.md`；
5. 只有满足该里程碑 Definition of Done 才进入下一阶段。

### 3.2 每次开始工作

必须先读：

- `HANDOFF.md`
- `TASKS.md`（若存在）
- `10_DECISIONS.md`
- 当前里程碑对应规格

然后输出一段很短的“本次目标”，之后直接施工，不重复重写整个项目。

### 3.3 每次结束工作

**必须更新 `HANDOFF.md`。没有 HANDOFF 的任务视为未完成。**

HANDOFF 至少包含：

- 当前里程碑；
- 已完成；
- 未完成；
- 本次修改文件；
- 构建命令与结果；
- 测试命令与结果；
- 已知问题；
- 新增/改变的协议；
- 安全影响；
- 下一步精确任务；
- “下一位 AI 不要重复做什么”。

如果因为环境原因没有运行构建/测试，必须明确写“未运行”及原因，绝不能伪造“通过”。

### 3.4 不允许自行发明密码学

允许：
- TLS；
- SHA-256；
- HMAC-SHA256；
- `RandomNumberGenerator`；
- `CryptographicOperations.FixedTimeEquals`；
- Windows DPAPI；
- .NET 自带 X509/SSL API。

不允许：
- 自己设计加密算法；
- XOR 混淆；
- 自写 AES 模式；
- 把访问密钥明文发给服务端；
- 通过 HTTP 明文传输；
- 因测试方便跳过证书指纹、同子网校验或 proof 校验。

### 3.5 不允许偷偷扩展需求

除非规格明确要求，否则不要添加：

- 用户系统；
- 数据库服务器；
- 云端 API；
- Web 后台；
- 外网穿透；
- UPnP；
- P2P 中继；
- 自动上传日志；
- 遥测；
- 广告；
- 文件传输；
- 剪贴板同步；
- 音频；
- 服务端常驻 Windows Service；
- 驱动；
- 内核组件。

## 四、代码结构要求

目标结构：

```text
LanRemote.sln
src/
  LanRemote.App/            # WPF UI、窗口、ViewModel、托盘、DI 启动
  LanRemote.Core/           # 领域模型、配置、公共接口
  LanRemote.Discovery/      # UDP 发现、网卡筛选、设备缓存
  LanRemote.Transport/      # TLS/TCP、帧协议、连接状态机
  LanRemote.Security/       # DPAPI、证书、访问密钥、认证挑战
  LanRemote.Capture/        # 屏幕采集、缩放、JPEG 编码
  LanRemote.Input/          # SendInput、坐标映射、权限闸门
  LanRemote.Sessions/       # Host/Client Session 编排
tests/
  LanRemote.Core.Tests/
  LanRemote.Security.Tests/
  LanRemote.Protocol.Tests/
  LanRemote.IntegrationTests/
docs/
  HANDOFF.md
  DECISIONS.md
  PROTOCOL.md
  SECURITY.md
scripts/
  configure-firewall.ps1
```

如果为了降低初期复杂度先放在较少项目中，仍必须保留清晰 namespace 与接口边界，并在里程碑完成后拆分。

## 五、核心接口先定义，后实现

至少定义：

```csharp
public interface IDiscoveryService
{
    IAsyncEnumerable<DiscoveredDevice> WatchAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public interface IScreenCaptureBackend
{
    IReadOnlyList<DisplayInfo> GetDisplays();
    ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken ct);
}

public interface IFrameEncoder
{
    ValueTask<EncodedFrame> EncodeAsync(
        CapturedFrame frame,
        VideoQualitySettings settings,
        CancellationToken ct);
}

public interface IRemoteInputInjector
{
    void MovePointer(NormalizedPoint point, DisplayInfo target);
    void MouseButton(MouseButtonEvent e, DisplayInfo target);
    void MouseWheel(MouseWheelEvent e, DisplayInfo target);
    void Keyboard(KeyboardEvent e);
}

public interface IAccessSecretStore
{
    AccessSecret LoadOrCreate();
    AccessSecret Regenerate();
}

public interface ISubnetPolicy
{
    bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress);
}
```

接口名可以轻微调整，但职责不能混乱。

## 六、网络与并发规则

- UDP 发现端口：`45872`。
- TLS/TCP 端口：`45873`。
- 多网卡时只使用启用的私有 IPv4 Ethernet / Wi-Fi 网卡。
- v1 默认排除 Loopback、Tunnel、虚拟网卡；以后可配置。
- 发现广播周期约 2 秒；
- 设备 6~8 秒未收到公告则标记离线/移除；
- TCP 连接接受后第一步就做本地/远端地址同子网校验；
- 不满足同子网立即断开；
- 每个远程会话至少两条 TLS 连接：
  - Control：认证、状态、键鼠、ping、质量控制；
  - Video：只传视频帧；
- 视频管线使用 bounded queue，容量 1~2，策略 DropOldest；
- 禁止无限增长队列；
- 所有 socket/stream 关闭必须可取消、可释放；
- 一个会话失败不能拖死其他会话；
- UI 线程不得进行阻塞网络 I/O 或图像编码。

## 七、身份与认证规则

### 7.1 设备码

- 首次启动生成随机 `deviceGuid`；
- 设备码可由 `SHA256(deviceGuid)` 前若干位编码成 8 个 Crockford Base32 字符；
- 展示形式如 `7K3M-P9QX`；
- 设备码不是秘密；
- 设备码只用于识别，不用于认证。

### 7.2 访问密钥

- 默认自动生成 128-bit 随机数；
- 使用 Base32 展示，约 26 字符，可分组显示；
- 不允许默认生成 6 位弱密码；
- 可以“重新生成”，生成后旧密钥立即失效；
- v1 不提供任意弱自定义密码；如以后支持自定义，必须单独设计抗离线猜测方案；
- 访问密钥 DPAPI 加密保存；
- 不写日志。

### 7.3 TLS

- 每台设备首次启动生成自签名 ECDSA P-256 证书；
- 私钥仅本机保存，使用 DPAPI 保护；
- 连接端根据发现包中携带的服务器证书 SHA-256 指纹进行 pinning；
- 自签名并不等于“无验证”；证书指纹不一致必须终止；
- 允许 TLS 1.2/1.3，由平台协商，但禁止旧 TLS。

### 7.4 认证挑战

绝不发送明文访问密钥。

参考流程：

1. TLS 建立；
2. Server 发 `AuthChallenge`；
3. Client 输入访问密钥；
4. Client 生成随机 `clientNonce`；
5. 双方拼相同 canonical transcript；
6. Client 发 `clientProof = HMACSHA256(accessKeyBytes, transcript)`；
7. Server 用本地访问密钥重算，固定时间比较；
8. Server 发 `serverProof = HMACSHA256(accessKeyBytes, "server|" + transcript)`；
9. Client 验证 serverProof；
10. 双向验证成功后，Server 生成随机 32-byte `sessionToken`，仅在 TLS 内发送；
11. Video 连接使用 sessionToken 做 attach proof。

理由：即使局域网内有人伪造发现包，客户端也不会把访问密钥本身交给伪造服务端；128-bit 随机访问密钥使观察到 proof 后进行离线穷举不可行。

## 八、UI 规则

主窗口至少：

- 本机设备名；
- 本机设备码；
- 访问密钥：默认遮挡、查看、复制、重新生成；
- “允许被发现”开关；
- “允许被控制”开关；
- “陌生控制端首次连接需本机确认”默认开启；
- “开机登录后启动”；
- 已发现设备列表；
- 设备名/设备码/IP/在线状态；
- 连接按钮。

连接窗口至少：

- 远端画面；
- 查看/控制状态；
- FPS；
- 缩放；
- JPEG 质量或预设；
- 全屏；
- 选择远端显示器；
- 网络 RTT；
- 实际接收 FPS；
- 当前分辨率；
- 重连；
- 断开。

多台远端设备必须是不同窗口，互不阻塞。

## 九、可靠性规则

- 所有 background loop 都必须接受 CancellationToken；
- 所有 IDisposable/IAsyncDisposable 必须正确释放；
- 断网必须进入明确的 Disconnected/Reconnecting 状态；
- 不允许 UI 假死；
- 视频帧延迟比完整性更重要：落后帧直接丢；
- 控制输入比视频优先；
- 捕获/编码异常只结束当前视频管线，不能直接杀进程；
- 未处理异常写本地日志并弹出可理解错误；
- 日志轮转，默认最多约 7 天；
- 错误信息不得泄露访问密钥。

## 十、完成定义

不要以“代码已经生成”作为完成标准。只有满足 `11_ACCEPTANCE_CHECKLIST.md`，才可宣称 v1 完成。

---

<!-- SOURCE: 02_PRODUCT_SPEC.md -->

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

---

<!-- SOURCE: 03_ARCHITECTURE.md -->

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

---

<!-- SOURCE: 04_PROTOCOL_AND_SECURITY.md -->

# 协议与安全规格

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

建议：

```text
LANREMOTE-AUTH-V1\0
sessionId\0
serverDeviceId\0
clientDeviceId\0
serverNonce(base64 canonical)\0
clientNonce(base64 canonical)\0
certSha256(uppercase hex)\0
requestedPermission(view|control)
```

Client：
```text
clientProof = HMAC-SHA256(accessKeyBytes, transcript)
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
serverProof = HMAC-SHA256(accessKeyBytes, UTF8("server\0") || transcript)
sessionToken = random 32 bytes
```

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

---

<!-- SOURCE: 05_UI_UX_SPEC.md -->

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

---

<!-- SOURCE: 06_DEV_STANDARDS.md -->

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

---

<!-- SOURCE: 07_MILESTONES_AND_TASKS.md -->

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

---

<!-- SOURCE: 08_TEST_PLAN.md -->

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

---

<!-- SOURCE: 09_HANDOFF_TEMPLATE.md -->

# HANDOFF.md 模板

> 规则：每次施工 AI 停止前必须把本模板内容更新到仓库根目录 `HANDOFF.md`。
> 不要只保留历史流水账；顶部必须始终是“当前状态”。

# LanRemote HANDOFF

## 1. 当前状态
- 时间：
- 当前里程碑：
- 当前分支/commit（如有）：
- 总体状态：进行中 / 被阻塞 / 里程碑完成

## 2. 本次目标
- 

## 3. 已完成
- [x] ...

## 4. 尚未完成
- [ ] ...

## 5. 本次修改文件
- `path/file.cs`：做了什么
- ...

## 6. 构建
执行：
```text
dotnet build ...
```

结果：
- PASS / FAIL / 未运行
- 如果 FAIL：精确错误摘要

## 7. 测试
执行：
```text
dotnet test ...
```

结果：
- x passed / y failed / 未运行
- 失败测试名：

## 8. 手工验证
- [ ] 双机发现
- [ ] TLS
- [ ] Auth
- [ ] 视频
- [ ] 控制
- [ ] 多窗口
- 只列本次实际验证的。

## 9. 协议变化
- 没有 / 有：
- message：
- field：
- version 是否变化：

## 10. 安全影响
- 是否涉及 access key：
- 是否涉及 TLS：
- 是否涉及 subnet：
- 是否新增 secret/log：
- 风险与处理：

## 11. 已知问题 / 技术债
1. ...
2. ...

## 12. 下一步（必须具体，按顺序）
1. 打开 `...` 实现 `...`
2. 为 `...` 添加测试
3. 执行 `dotnet test ...`

## 13. 下一位 AI 不要重复做
- `SubnetPolicy` 已完成，不要重写，除非测试失败。
- ...

## 14. 关键上下文
- 解释下一位 AI 若不知道就容易做错的 3~8 个事实。

---

<!-- SOURCE: 10_DECISIONS.md -->

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

---

<!-- SOURCE: 11_ACCEPTANCE_CHECKLIST.md -->

# v1 发布验收清单

只有全部“必须”项通过，才可称 v1 完成。

## A. 构建与安装
- [ ] Release build 通过
- [ ] tests 通过
- [ ] self-contained x64 可在无 SDK 电脑启动
- [ ] 配置目录正确
- [ ] firewall 脚本可安装/移除规则

## B. 身份
- [ ] 每台设备有稳定 device code
- [ ] 每台设备有独立 128-bit access key
- [ ] key 默认遮挡
- [ ] key 可重置
- [ ] 重置后旧 key 立即失效
- [ ] key 不在日志/config 明文

## C. 发现
- [ ] 同 /24 两台设备 3~6 秒互相发现
- [ ] 停止 app 后 6~8 秒离线
- [ ] device list 不重复
- [ ] malformed discovery 不崩

## D. 网络范围
- [ ] 只监听私有 IPv4 NIC
- [ ] 同 subnet 允许
- [ ] 不同 subnet TCP 被拒绝
- [ ] 不做公网 relay/NAT/UPnP
- [ ] firewall RemoteIP=LocalSubnet

## E. TLS/Auth
- [ ] TLS 启用
- [ ] cert fingerprint pinning
- [ ] raw key 从未发送
- [ ] wrong key fail
- [ ] serverProof 验证
- [ ] auth failure rate limit
- [ ] video attach token
- [ ] token 过期/乱用拒绝

## F. 视频
- [ ] 主显示器可看
- [ ] 多显示器可切换
- [ ] 5/10/15/20/30 FPS
- [ ] 50/67/75/100% scale
- [ ] JPEG quality
- [ ] queue DropOldest
- [ ] 慢网不会无限积压
- [ ] 30 分钟无明显持续内存增长

## G. 控制
- [ ] ViewOnly 不能控制
- [ ] 未认证不能控制
- [ ] AllowControl=false 不能控制
- [ ] 鼠标 move/click/wheel
- [ ] 键盘基础按键
- [ ] letterbox 坐标准确
- [ ] local emergency stop
- [ ] 被控状态有明显本地指示

## H. 多会话
- [ ] 一个进程同时连接至少 2 台
- [ ] 各自独立窗口
- [ ] 独立画质
- [ ] 一个断开不影响其他

## I. 故障处理
- [ ] 断网不死锁
- [ ] host 退出客户端能感知
- [ ] client 退出 host 清理 session
- [ ] close window 正确释放
- [ ] no unbounded Task/socket/GDI handle growth

## J. 文档/HANDOFF
- [ ] README 使用说明
- [ ] SECURITY 说明
- [ ] PROTOCOL 说明
- [ ] HANDOFF 最新
- [ ] DECISIONS 最新
- [ ] 没有虚假的“测试通过”记录

---

<!-- SOURCE: AGENTS.md -->

# AGENTS.md — 给所有编码代理

你在 LanRemote 仓库工作时必须遵守：

1. 先读 `HANDOFF.md`，再读当前 milestone。
2. 不得删除：
   - TLS
   - certificate pinning
   - same-subnet policy
   - HMAC auth proof
   - DPAPI secret storage
   - local approval
   - remote control indicator
3. 不得把 access key 明文发送或写日志。
4. 不得增加云端、登录、公网穿透、UPnP、中继。
5. 所有网络读取先做长度上限验证。
6. 所有 background loops 都能 CancellationToken 停止。
7. 视频队列 bounded + DropOldest。
8. UI thread 不做 blocking I/O/encoding。
9. 改协议必须同时更新协议文档和测试。
10. 每次结束必须更新 `HANDOFF.md`。
11. 没跑过 build/test 就写“未运行”，绝不伪造。
12. 任何“为了临时测试关闭安全”的代码，任务结束前必须删除；最好用 dependency injection/fake 实现，不要改 production policy。

---

<!-- SOURCE: PROJECT_BOOTSTRAP.md -->

# 项目初始化参考

> 命令仅为建议，施工 AI 应按实际 SDK/目录微调。

```powershell
dotnet new sln -n LanRemote

dotnet new wpf -n LanRemote.App -f net10.0-windows
dotnet new classlib -n LanRemote.Core -f net10.0
dotnet new classlib -n LanRemote.Discovery -f net10.0
dotnet new classlib -n LanRemote.Transport -f net10.0
dotnet new classlib -n LanRemote.Security -f net10.0-windows
dotnet new classlib -n LanRemote.Capture -f net10.0-windows
dotnet new classlib -n LanRemote.Input -f net10.0-windows
dotnet new classlib -n LanRemote.Sessions -f net10.0

dotnet new xunit -n LanRemote.Core.Tests -f net10.0
dotnet new xunit -n LanRemote.Security.Tests -f net10.0
dotnet new xunit -n LanRemote.Protocol.Tests -f net10.0
dotnet new xunit -n LanRemote.IntegrationTests -f net10.0
```

推荐全局属性 `Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
  </PropertyGroup>
</Project>
```

说明：
- 初期 `TreatWarningsAsErrors=false`，避免 Win32/WPF interop 警告阻塞施工；
- M9 前应把项目自身可修警告清掉；
- 不要用 suppress-all 方式隐藏安全警告。

## 推荐领域模型

```csharp
public sealed record DeviceIdentity(
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    string CertificateSha256);

public sealed record DiscoveredDevice(
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    IPAddress Address,
    int Port,
    string CertificateSha256,
    DateTimeOffset LastSeen,
    IReadOnlySet<string> Capabilities);

public enum SessionPermission
{
    ViewOnly = 0,
    Control = 1,
}

public sealed record VideoQualitySettings(
    int TargetFps,
    double Scale,
    int JpegQuality,
    bool Auto);
```

## Config 草案

```json
{
  "allowDiscovery": true,
  "allowViewing": true,
  "allowControl": true,
  "requireLocalApprovalForUnknownController": true,
  "autoStartOnLogin": false,
  "defaultQualityPreset": "Balanced",
  "discoveryPort": 45872,
  "transportPort": 45873,
  "maxViewSessions": 3,
  "maxControlSessions": 1
}
```

秘密不在这个 JSON。
