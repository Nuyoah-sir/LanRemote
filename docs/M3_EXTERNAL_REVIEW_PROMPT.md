# M3 外部评审 prompt（供转发给国外模型）

> 用途：M3 开工前的**设计红队评审**。
> 立场：只问「我漏了什么」，**不问**「Windows/.NET 实际行为是什么」——后者本机实测可定案，
> 模型的回忆不可作为 HANDOFF 里的结论（项目硬规矩：严禁伪结论）。
> 状态：**Prompt A 已转发并回收**（→ `docs/M3_REVIEW_TRIAGE.md`，约束已并入 M3 实现）。
> Prompt B（条件性）：条件已由本机 spike 满足并**本机定案**（ADR-029/030，`EphemeralKeySet` 被证伪），未再外发。

---

## Prompt A — M3 设计红队评审（主 prompt，可直接转发）

```text
You are reviewing the design of one milestone of a personal LAN remote-desktop tool.
I want criticism and omission-finding, not reassurance. Be concrete and skeptical.

=== CONTEXT ===

Product: Windows-only, LAN-only screen sharing / remote control for personal use,
between two PCs owned by the same user.

Hard constraints (do NOT propose violating any of these):
- No cloud, no account system, no CA/PKI, no relay, no UPnP/NAT traversal, no mDNS.
- IPv4 only. Peers must be RFC1918 AND in the SAME IPv4 subnet as the listener's local endpoint.
- Crypto allowlist only: TLS, SHA-256, HMAC-SHA256, RandomNumberGenerator,
  CryptographicOperations.FixedTimeEquals, Windows DPAPI, BCL X509/SslStream. No custom crypto.

Stack: C# / .NET 10 / WPF, x64, self-contained publish. Targets Windows 10 22H2 / Windows 11.

Two INDEPENDENT TLS/TCP connections per session: Control (port 45873) and Video (own connection).
This milestone covers Control only; Video is a later milestone.

Discovery (already implemented, previous milestone, unauthenticated):
- UDP 45872, IPv4 multicast plus per-NIC directed broadcast probe.
- Announcement payload: deviceId, displayName, capabilities, certSha256 (uppercase hex, SHA-256 of cert.RawData).
- Peer address is ALWAYS taken from the UDP source endpoint; there is no address field in the payload
  and payload addresses must never be trusted.

Device identity (already implemented):
- Self-signed ECDSA P-256 certificate, 5 years, EKU serverAuth, KeyUsage digitalSignature ONLY, non-CA.
- Private key lives in a DPAPI-protected file (secrets.bin).
- Fingerprint = SHA256(cert.RawData), uppercase hex, stable across reloads.
- Loaded via X509CertificateLoader.LoadPkcs12(pfx, password, EphemeralKeySet).

Scope of THIS milestone (M3):
1. One TCP listener per eligible private NIC on port 45873.
2. On accept: verify socket LocalEndPoint address and RemoteEndPoint address are in the same subnet;
   if not, close immediately.
3. SslStream server using the device certificate above. TLS 1.2 and 1.3 allowed;
   SSL3 / TLS 1.0 / TLS 1.1 forbidden.
4. Client pins the server certificate: inside the certificate validation callback it compares the
   presented certificate's SHA-256 fingerprint to the fingerprint learned from discovery,
   using CryptographicOperations.FixedTimeEquals. Unconditional "return true" is forbidden.
5. Length-prefixed JSON framing: [4-byte big-endian uint32 length][length bytes of UTF-8 JSON].
   Control message max 1 MiB; over-limit disconnects immediately and logs.
6. A channel_hello message.

OUT of scope for this milestone: access-key authentication. A later milestone adds an
HMAC-SHA256 challenge/response bound to a canonical transcript, plus server proof.
Assume for this review that after the TLS handshake there is NO authentication yet.

=== WHAT I WANT FROM YOU ===

1. Attacks and failure modes I have not covered. In particular:
   - What can an attacker who is merely on the same subnet do?
   - What can an attacker who can SPOOF the UDP discovery announcement do?
   - What does fingerprint pinning actually buy here, and what does it explicitly NOT buy?

2. Ordering: should the same-subnet check run on the raw accepted socket BEFORE the TLS handshake?
   Any downside (information leak, or is it pointless because such a peer could not connect anyway)?
   Should the subnet decision be re-verified after the handshake or on the first message?

3. Pinning mechanics in .NET: is validating ONLY the fingerprint (ignoring chain, name, expiry)
   acceptable under this threat model? What is the smallest set of additional checks you would insist on
   (e.g. key algorithm is ECDSA P-256, not expired, basic constraints non-CA)?
   What specifically breaks if expiry is ignored?

4. Listener robustness / DoS: backlog size, handshake timeout, read timeout, what to do with a client
   that connects and never sends anything, per-peer and global simultaneous connection limits,
   and how the 1 MiB cap should behave when a peer dribbles bytes very slowly.

5. Framing: enumerate the exact tests a length-prefixed reader needs (partial reads, TCP segmentation,
   length == 0, length > 1 MiB, length large enough to overflow a signed int, invalid UTF-8,
   cancellation mid-read, disposal during read).

6. Anything about TLS 1.2 vs TLS 1.3 with self-signed ECDSA P-256 certificates on Windows (SChannel)
   that has actually bitten you. For each claim, clearly label it as VERIFIED KNOWLEDGE vs RECOLLECTION;
   I will test locally and cannot accept recollection as fact.

7. Pre-authentication state safety:
   - The fingerprint comes from unauthenticated UDP discovery, so TLS pinning must NOT be treated as
     proof that this is the user's intended device.
   - Review whether M3 needs an explicit PRE_AUTHENTICATED state in which the only permitted
     application message is the minimum protocol/channel negotiation needed for M4.
   - Identify anything that must NOT be exposed or enabled before the later access-key proof succeeds.
   - In particular, look for accidental authorization such as screen data, input capability,
     session tokens, privileged host metadata, or permission decisions being reachable after TLS
     but before application authentication.
   - Give the smallest test that proves the M3 TLS connection is operational but still
     application-level unauthenticated.
   (Note: our spec DoD already states "no session may be entered before auth"; I want to know what
   that requires in CODE, not just in intent.)

8. Discovery-to-connection TOCTOU:
   - When the user clicks Connect, should the client freeze an immutable connection snapshot:
       { deviceId, source IPv4, tcpPort, certSha256 }
     and use that exact snapshot for the entire TCP/TLS attempt?
   - What can go wrong if the live discovery cache is allowed to change the expected fingerprint
     or endpoint while ConnectAsync is already running?
   - Give the smallest race test that would catch this.

9. Conflicting discovery identities:
   - Consider two valid-looking announcements seen close together:
       same deviceId, but different source IP and/or different certSha256.
   - MEASURED FACT in our current code: the discovery cache is a Dictionary keyed by deviceId (Guid)
     and Upsert is last-write-wins, so a conflicting announcement silently replaces IP and fingerprint.
   - Careful: the SAME device legitimately announces the same deviceId from DIFFERENT source IPs when it
     has multiple eligible NICs (fingerprint identical). Please separate
       "same deviceId + SAME fingerprint + different IP"  (benign, multi-NIC)
     from
       "same deviceId + DIFFERENT fingerprint"            (suspicious).
     Do not propose a rule that would reject the benign multi-NIC case.
   - No persistent CA/trust-store solution may be introduced.
   - Should this be last-writer-wins, treated as an identity conflict, or otherwise surfaced?
   - What is the minimum behavior needed to avoid silently turning an identity collision into a
     connection to a different certificate?
   - Separate what M3 can defend from what M4's access-key server proof will ultimately authenticate.

10. Deadline semantics:
   - Distinguish a per-read timeout from an ABSOLUTE deadline for a protocol stage.
   - Could a peer keep a connection forever by sending one byte just before every read timeout?
   - Review separate total deadlines for:
       TCP connect,
       TLS handshake,
       4-byte frame header,
       frame payload,
       first channel_hello.
   - Review whether global/per-IP connection limits must be acquired immediately after accept,
     BEFORE an expensive TLS handshake.
   - Treat the listen backlog only as a pending-connect queue parameter, not as the primary DoS limit.
   - Note: SslStream exposes AuthenticateAsServerAsync/AuthenticateAsClientAsync overloads that take a
     CancellationToken; handshakes do not carry the timeout semantics an application wants by default.

11. Strict application parser:
   Review and give tests for:
   - checking uint32 length against MaxMessageSize BEFORE any cast to int;
   - length == 0;
   - invalid UTF-8 with no replacement-character fallback;
   - JSON maximum nesting depth;
   - comments/trailing commas;
   - duplicate JSON property names and ambiguous "type"/"channel" fields;
   - channel_hello for any channel other than "control" during M3;
   - EOF after 1, 2, or 3 bytes of the length prefix;
   - EOF partway through the payload;
   - cancellation/disposal races;
   - multiple complete frames coalesced in one TCP receive;
   - a second frame arriving immediately after channel_hello.

12. Pin source and comparison representation:
   - Review whether the expected discovery fingerprint should be decoded once into exactly 32 bytes
     before TCP connect and then compared against SHA256(presentedCert.RawData) as bytes.
   - Reject malformed expected fingerprints before opening the connection.
   - Do not compare formatted hex strings as the security decision.
   - Explain whether certificate-profile checks (ECDSA P-256, non-CA, EKU serverAuth,
     KeyUsage digitalSignature, validity window) are actual independent security requirements
     once an exact SHA-256 certificate pin matches, or primarily invariant / misconfiguration checks.
   - Explicitly separate those two categories.

=== ONE ASSERTION I WANT YOU TO CHALLENGE ===

"Because the discovery channel is unauthenticated, a fingerprint learned from discovery is not by
itself a root of trust. Pinning prevents a different certificate from being substituted AFTER the
client has selected that discovery record, but a party able to forge the selected discovery record
may cause the client to pin the attacker's certificate. The later access-key mutual proof is what
authenticates possession of the intended device secret."

State precisely where this assertion is correct, incomplete, or wrong.

=== RULES FOR YOUR ANSWER ===
- Do not propose adding a CA, a trust store, a config toggle to disable pinning, or any cloud/relay component.
- Do not propose any "return true" style bypass or any fallback that accepts an unpinned certificate.
- Answer as a numbered list of concrete gaps. For each gap give: what goes wrong,
  and the smallest test that would catch it.

=== LABEL EVERY CLAIM WITH EXACTLY ONE OF THESE TAGS ===
- VERIFIED KNOWLEDGE   -- you are confident and can point at the mechanism/spec.
- NEEDS LOCAL EXPERIMENT -- depends on the specific Windows build + .NET 10 + SChannel combination;
                            must be settled by running code on the target machine. Give the minimal experiment.
- RECOLLECTION         -- you believe it but cannot verify it; we will treat it as a hint only.

=== OUTPUT STRUCTURE (so I can triage mechanically) ===
Return three sections, in this order:
  A. DESIGN MUST-FIX      -- issues I should change in the design before writing code.
  B. WORTH A TEST         -- behaviors to pin down with an explicit test (local experiment acceptable).
  C. UNVERIFIED / HINTS   -- things I must NOT record as fact until measured locally.
```

---

## Prompt B — 条件性（**先别发**）

只有当本机 spike 失败时才用：ADR-018 的未关闭风险是「Windows 上 SslStream **服务端**使用
`EphemeralKeySet` 载入的 ECDSA P-256 私钥是否可靠」。本机实测能定案，模型的回忆不能。
若 spike 报错，再带着**真实异常原文**去问：

```text
Environment (paste MEASURED values only -- do not let anyone fill these in from memory):

- OS:                <paste result of: (Get-CimInstance Win32_OperatingSystem).Caption>
- OS build:          <paste result of: (Get-CimInstance Win32_OperatingSystem).BuildNumber>
- NOTE: on Windows 11 the registry value HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProductName
        still reads "Windows 10 Pro". Do NOT infer the OS name from that registry value.
- .NET runtime:      <paste: dotnet --version, and the TFM / publish mode actually used>
- Publish mode:      <self-contained x64 ? framework-dependent ? single-file ?>
- Certificate:       self-signed ECDSA P-256, EKU serverAuth, KeyUsage digitalSignature only, non-CA
- Loaded with:       X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet)
- Role:              SERVER certificate of an SslStream over a loopback TCP listener
- SslProtocols:      <paste the exact value passed>
- Failure:           <paste the exact exception type, message, and inner exception>

Question: is there a known SChannel constraint that makes EphemeralKeySet unusable for a TLS
SERVER certificate on this Windows build, and if so what is the smallest flag change that keeps the
private key OFF disk after process exit?
Label every claim VERIFIED KNOWLEDGE / NEEDS LOCAL EXPERIMENT / RECOLLECTION.
```

> ⚠️ 环境一律**实测粘贴**，不许预填。原因就是本文件这次踩到的坑：
> 我先前只读了注册表 `ProductName=Windows 10 Pro` 就写下「Windows 10 Pro 25H2 / build 26200」，
> 这个组合不成立——实测 `Win32_OperatingSystem.Caption` = **Microsoft Windows 11 专业版**，
> build 26200 是 **Windows 11 25H2**。让施工 AI 拿着错的环境去写 ADR 会污染整份文档。

---

## 明确不要问模型的三类问题

1. **Windows 实际行为**（防火墙规则、网卡选择、改 IP 会不会断网、`netsh` 退出码）
   —— 本机实测才是事实来源。昨天这类问题靠「以为是这样」已经断过两次网。
2. **本机 / 本项目的实测结论**（ADR-018 的 EphemeralKeySet 是否可用、TLS 1.3 在本机能否握手）
   —— 有本机 oracle，30 分钟出结果，且 ADR-018 本来就强制要求这条集成测试。
3. **规格里已经写死的东西**（端口、网段限制、密码学白名单、不能 `return true`）
   —— 问了只会得到建议放宽约束的答案，而那些是不可动摇的。

---

# ⛔ 以下为**本机立场**，不要随 prompt 外发

写在这里是为了：(a) 给下一位施工 AI 一个可对照的基线；(b) 不让评审者看到我的答案而被锚定。
下列结论中标注「实测」的来自本机测量，其余是我的工程判断。

## 本机 OS 实测（2026-09-21，修正了我上一轮的错误）

```
Win32_OperatingSystem.Caption = Microsoft Windows 11 专业版
Win32_OperatingSystem.Version = 10.0.26200        BuildNumber = 26200
注册表 ProductName            = Windows 10 Pro    ← Windows 11 的已知遗留值，读它会误判
注册表 DisplayVersion         = 25H2              EditionID = Professional
```

→ 本机是 **Windows 11 25H2 / build 26200**，**不是** Windows 10。外部评审这条纠正是对的。
→ **陷阱**：`HKLM:\...\CurrentVersion\ProductName` 在 Windows 11 上仍是 `Windows 10 Pro`；
 判定系统版本必须用 `Win32_OperatingSystem.Caption`，不能用这个注册表值。已记入项目记忆。
→ 影响：ADR-001 目标「Windows 10 22H2 / Windows 11」仍覆盖本机，无冲突；
  但 M3 任何「在 Win10 22H2 上的行为」都不能靠本机外推，需要另找机器或标注未测。

## 对那条命题的判断（正确 / 不完整 / 需注意）

**正确**：
- 指纹来自未认证 discovery，因此 pin **不是** first-contact 的信任根；真正的信任根是
  **用户带外传递的 access key**（M4 的双向 HMAC proof）。
- pin 能证明的只有「TLS 对端持有我在连接那一刻所选记录里那张证书的私钥」。

**不完整**（外部评审漏掉的三点）：
1. **前置闸门**：攻击者还得先跨过 RFC1918 + 同子网校验，即必须是**链路内**攻击者，
   且持有被选记录对应证书的私钥。伪造整条 discovery 记录的可行性被限制在二层可达范围内。
2. **时间维度 / 持久化**：pin 只在本次连接生效、**不持久化**。真正的危险是未来若加「记住此设备」
   而把 discovery 来的指纹持久成信任（TOFU 持久化）——**这条必须写死禁止**。
3. **M4 的 transcript 绑定**：规格 `04_PROTOCOL_AND_SECURITY.md` 的 M4 测试清单里已有
   「modified cert fingerprint fail」，说明 canonical transcript 会绑定证书指纹。
   因此完整身份 = access key 持有 ∧ 指纹一致，两者缺一不可；缺 pin 则 key 可被中继，
   缺 key 则 pin 只是"连对了证书"而非"连对了用户想连的设备"。

**需注意（危害等级要精确）**：
- 「攻击者可诱导客户端 pin 到攻击者的证书」成立，但**攻击者仍然过不了 M4**。
  所以在 M3 阶段，它的后果是「**可能被诱导连错对象**」，而**不是**「已获得授权」。
  记录时必须区分这两种危害等级，否则会被误读成 M3 有可远程利用的授权绕过。
- pin 不只防攻击，也防**误操作**（同子网里恰好另一台也在广播，用户选错）。

**对第 12 点「profile 检查是独立安全要求还是不变量检查」我的预判**：
- 当 SHA-256 全等 pin 匹配成立时，ECDSA P-256 / non-CA / EKU / KeyUsage 属于
  **invariant / 误配置检查**，不构成独立安全边界（能伪造出同指纹证书等于已攻破 SHA-256 碰撞）。
- **validity window 是例外**：过期证书照样 pin 得上。它是我们自己的签发策略不变量，
  不能当安全边界依赖，但必须检查并告警——否则 5 年到期后会静默地继续放行。

## 我对 7–12 的取舍

| # | 我的判断 |
|---|---|
| 7 pre-auth inert | **接受，且规格已部分覆盖**：M3 DoD 已写「没有 auth 前不能进入 Session」。缺的是**代码层面怎么强制**。M3 必须显式有 PRE_AUTHENTICATED 状态，除 channel_hello / 协议协商外不暴露任何能力 |
| 8 TOCTOU 快照 | **接受**。连接目标必须是不可变快照 `{deviceId, IP, port, certSha256}`，握手期间不许被 discovery 更新替换 |
| 9 身份冲突 | **部分接受，且要补强**：我查了代码，`DiscoveryDeviceCache` 是 `Dictionary<Guid,...>`、`Upsert` last-write-wins → 同 deviceId 换 IP/换指纹确实静默覆盖（**实测代码事实**）。但不能一律判冲突：**多网卡时同 deviceId + 同指纹 + 不同 IP 是良性的**。真正可疑的只有「同 deviceId + 不同指纹」。归属：M3 只做第 8 点的快照；改缓存语义属 **M4/M9**，且**必须单独立 ADR**，不得在 M3 顺手改 M2 已验收行为 |
| 10 deadline | **接受**。每阶段 absolute deadline，不是 per-read 续期；限额 semaphore 必须在 accept 后、握手前占用；backlog 只是待 accept 队列，不是 DoS 防线（这点外部评审是对的） |
| 11 strict parser | **接受**。`length==0` 我倾向**明确拒绝**（control 消息没有合法空负载）；`uint` 比较先于 `int` 转换必须写进测试 |
| 12 pin 表示 | **接受**。先解码成 32 字节、连之前就校验格式、按字节比较，不比 hex 字符串 |

## 三桶分类怎么用

评审回来后按 A/B/C 三桶处理：
- **A 设计必须修** → 进 M3 设计，写进 HANDOFF
- **B 值得测试** → 变成 M3 测试用例
- **C 未验证 / 回忆** → **不得写进 HANDOFF 的事实陈述**；要么本机实测升级成 A/B，要么丢弃
