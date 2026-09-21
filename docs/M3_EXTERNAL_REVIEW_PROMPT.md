# M3 外部评审 prompt（供转发给国外模型）

> 用途：M3 开工前的**设计红队评审**。
> 立场：只问「我漏了什么」，**不问**「Windows/.NET 实际行为是什么」——后者本机实测可定案，
> 模型的回忆不可作为 HANDOFF 里的结论（项目硬规矩：严禁伪结论）。
> 状态：**Prompt A 待转发**；Prompt B 是条件性的，**先不要发**，等本机 spike 出结果再说。

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

=== RULES FOR YOUR ANSWER ===
- Do not propose adding a CA, a trust store, a config toggle to disable pinning, or any cloud/relay component.
- Do not propose any "return true" style bypass or any fallback that accepts an unpinned certificate.
- If something must be verified empirically on Windows/.NET 10 rather than asserted, say so explicitly
  and describe the MINIMAL experiment that settles it.
- Answer as a numbered list of concrete gaps. For each gap give: what goes wrong,
  and the smallest test that would catch it.
```

---

## Prompt B — 条件性（**先别发**）

只有当本机 spike 失败时才用：ADR-018 的未关闭风险是「Windows 上 SslStream **服务端**使用
`EphemeralKeySet` 载入的 ECDSA P-256 私钥是否可靠」。本机实测能定案，模型的回忆不能。
若 spike 报错，再带着**真实异常原文**去问：

```text
Environment (measured, not assumed):
- Windows 10 Pro 25H2, build 26200
- .NET 10 (self-contained x64)
- ECDSA P-256 self-signed certificate, EKU serverAuth, KeyUsage digitalSignature only, non-CA
- Loaded with X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet)
- Used as the SERVER certificate of an SslStream over a loopback TCP listener
- <paste the exact exception type, message, and inner exception here>
- SslProtocols used: <paste>

Question: is there a known SChannel constraint that makes EphemeralKeySet unusable for a TLS
SERVER certificate on Windows, and if so what is the smallest flag change that keeps the private key
OFF disk after process exit? Label each claim VERIFIED vs RECOLLECTION.
```

---

## 明确不要问模型的三类问题

1. **Windows 实际行为**（防火墙规则、网卡选择、改 IP 会不会断网、`netsh` 退出码）
   —— 本机实测才是事实来源。昨天这类问题靠「以为是这样」已经断过两次网。
2. **本机 / 本项目的实测结论**（ADR-018 的 EphemeralKeySet 是否可用、TLS 1.3 在本机能否握手）
   —— 有本机 oracle，30 分钟出结果，且 ADR-018 本来就强制要求这条集成测试。
3. **规格里已经写死的东西**（端口、网段限制、密码学白名单、不能 `return true`）
   —— 问了只会得到建议放宽约束的答案，而那些是不可动摇的。
