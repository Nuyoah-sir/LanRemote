# START HERE — LanRemote 施工代理第一入口

你现在接手的是 **LanRemote** 项目。

这是一个 Windows 局域网屏幕共享与远程控制工具。当前仓库中提供的是完整规格与施工约束，代码尚未开始或可能处于某个施工阶段。

## 你的第一原则

**不要先凭经验重写方案。先读文件，再施工。**

你必须把仓库中的规格视为项目合同。除非规格内部明确允许，否则不得擅自删除、弱化或替换安全要求。

---

# 第一次接手时必须按以下顺序执行

## Step 1 — 阅读

按顺序完整阅读：

1. `START_HERE.md`
2. `00_README.md`
3. `01_MASTER_PROMPT.md`
4. `HANDOFF.md`
5. `10_DECISIONS.md`
6. `02_PRODUCT_SPEC.md`
7. `03_ARCHITECTURE.md`
8. `04_PROTOCOL_AND_SECURITY.md`
9. `05_UI_UX_SPEC.md`
10. `06_DEV_STANDARDS.md`
11. `07_MILESTONES_AND_TASKS.md`
12. `08_TEST_PLAN.md`
13. `11_ACCEPTANCE_CHECKLIST.md`
14. `AGENTS.md`
15. `PROJECT_BOOTSTRAP.md`

以后继续施工时，不需要每次重新逐字阅读全部文件，但每次开始必须至少重新读取：

- `HANDOFF.md`
- `AGENTS.md`
- 当前 milestone 对应的规格文件
- `10_DECISIONS.md` 中相关 ADR

---

# Step 2 — 判断当前项目状态

以 `HANDOFF.md` 为主要状态来源，同时检查实际仓库文件。

禁止只相信 HANDOFF 而不检查真实代码。

如果 HANDOFF 与代码不一致：

1. 以真实仓库状态为准；
2. 修正 `HANDOFF.md`；
3. 明确记录不一致之处；
4. 不要在错误状态上继续施工。

---

# Step 3 — 确定当前里程碑

严格使用：

`07_MILESTONES_AND_TASKS.md`

中的 M0 → M11 顺序。

如果当前还是空项目：

**只执行 M0。**

不要直接实现：

- 屏幕共享；
- 键盘控制；
- H.264；
- WebRTC；
- QUIC；
- NAT traversal；
- UPnP；
- 云服务；
- 用户系统。

---

# Step 4 — 第一次施工

如果仓库尚无代码：

执行 **M0 — 仓库骨架**。

目标：

- 创建 `LanRemote.sln`
- 创建规范中的 projects
- 创建 WPF 主程序
- 创建基础 DI / logging / config
- 创建测试项目
- 建立单实例机制
- 保证可以 build
- 保证 tests 可以运行

完成后才进入 M1。

---

# Step 5 — 每一个里程碑的固定流程

每个里程碑必须按：

```text
READ
  ↓
DESIGN CHECK
  ↓
IMPLEMENT
  ↓
BUILD
  ↓
TEST
  ↓
MANUAL CHECK（能执行时）
  ↓
UPDATE DOCS
  ↓
UPDATE HANDOFF.md
```

禁止：

```text
IMPLEMENT M1~M8
→ 最后一次 build
```

必须逐阶段验证。

---

# 强制安全规则

以下项目是硬约束。

任何时候都不得为了“先跑起来”删除：

- 同 IPv4 subnet 校验
- RFC1918 私网限制
- TLS
- certificate fingerprint pinning
- Access Key challenge proof
- server proof
- DPAPI secret storage
- Video attach session token
- ViewOnly / Control 权限隔离
- 本机连接审批
- 被控状态提示
- 本机紧急停止

禁止：

- 明文发送 Access Key
- 日志记录 Access Key
- 无条件接受 TLS certificate
- `return true` 绕过证书验证
- 6 位默认弱密码
- HTTP 明文控制
- UPnP
- NAT port mapping
- 公网 relay
- 云端 API
- 第三方中继服务
- 为测试永久关闭认证

如果测试需要绕过真实环境：

使用 dependency injection / mock / fake。

不要修改 production security policy。

---

# 网络设计不得自行变更

v1：

```text
UDP 45872
    ↓
LAN discovery

TCP/TLS 45873
    ├── Control connection
    └── Video connection
```

Control 与 Video 使用独立 TLS/TCP。

原因：

视频大包不能阻塞鼠标和键盘控制。

不要自行合并成一个 socket，除非以后有 ADR 正式替换该决定。

---

# 视频设计不得过度施工

v1：

- GDI BitBlt capture
- scale
- JPEG encode
- bounded Channel
- DropOldest
- TLS video stream

不要一开始写：

- H.264 Media Foundation
- NVENC
- QuickSync
- AMF
- DirectX capture pipeline
- WebRTC

这些属于后续性能优化。

---

# 实时视频必须遵守

远控优先：

**实时性 > 每一帧完整送达**

因此必须：

```text
Capture
  ↓
bounded queue (1~2)
DropOldest
  ↓
Encode
  ↓
bounded queue (1~2)
DropOldest
  ↓
Network
```

严禁 unbounded queue。

如果网络跟不上：

丢旧帧。

不要积累 10 秒旧画面。

---

# 输入安全闸门

只有以下条件全部成立才允许调用 `SendInput`：

```text
session authenticated
AND
session permission == Control
AND
Host.AllowControl == true
AND
local approval granted control
AND
session is active
```

否则所有输入消息直接拒绝。

不要只依赖客户端“不发送”。

服务端必须再次验证。

---

# AI 开发行为规范

你是持续开发代理，而不是“一次性代码生成器”。

每次工作：

## 开始时

简短说明：

```text
Current milestone:
Goal:
Files expected to change:
Tests expected:
```

之后直接施工。

不要写几页计划而不动代码。

## 结束时

必须：

1. build
2. test
3. 更新 docs
4. 更新 `HANDOFF.md`

最终回复必须给：

```text
Milestone:
Implemented:
Build:
Tests:
Manual verification:
Known issues:
Next exact task:
```

---

# HANDOFF.md 是硬要求

每次停止工作前必须更新。

即使：

- 上下文即将用完；
- 用户要换模型；
- 遇到 bug；
- 当前 milestone 没做完；

也必须写。

HANDOFF 必须让另一个完全不认识当前上下文的 AI 能继续施工。

绝不只写：

> 后续继续优化。

而要写：

> M3 已完成 TCP listener 和 TLS，尚未实现 AuthChallenge。
> 下一步打开 `HostConnection.cs` 实现 AuthChallenge，然后为 wrong-key 添加 integration test。
> `SubnetPolicy.cs` 已完成并有 14 个测试，不要重新实现。

---

# 不允许伪造验证结果

如果没有执行：

```text
dotnet build
```

必须写：

```text
Build: NOT RUN
```

如果没有执行：

```text
dotnet test
```

必须写：

```text
Tests: NOT RUN
```

不能根据代码“看起来没问题”声称 PASS。

---

# 出现规格冲突时

优先级：

1. 用户最新明确指令
2. `04_PROTOCOL_AND_SECURITY.md`（涉及安全时）
3. `01_MASTER_PROMPT.md`
4. `10_DECISIONS.md`
5. `07_MILESTONES_AND_TASKS.md`
6. 其他规格

若仍无法解决：

- 选择安全且最小改动方案；
- 记录到 `10_DECISIONS.md`；
- 在 HANDOFF 中指出。

不要擅自大规模改架构。

---

# 第一次给施工 AI 的具体任务

如果当前代码仓库为空，请现在：

1. 完整读取上述文件；
2. 检查实际目录；
3. 确认 `HANDOFF.md` 显示当前为 M0；
4. 实施 M0；
5. 运行 build/tests；
6. 修复直到 M0 DoD 满足，除非存在无法解决的环境阻塞；
7. 更新 `HANDOFF.md`；
8. 停止，不要自动跳到 M1，除非用户明确要求连续施工。

**现在开始。**
