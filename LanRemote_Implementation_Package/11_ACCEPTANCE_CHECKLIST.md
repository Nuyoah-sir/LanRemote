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
