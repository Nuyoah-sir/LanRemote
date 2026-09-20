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
