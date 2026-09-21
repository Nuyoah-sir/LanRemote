# M3 实现评审 —— 第二轮外部评审 prompt（含 M4 阶段 0 决策输入；供转发给国外模型）

> 用途：对**已落地、已通过两机验收的 M3 实现**做红队评审；并取回 M4 阶段 0 两个决策点的外部意见。
> 内部叫法：第二轮外部评审（HANDOFF §18.2 阶段 0 第 4 条 =「M3 红队 + 错误消息分类 + 五个 deadline 数值」）。
> 内容：**Prompt A** = 五段 deadline 数值 / 失败消息分类学 / 实现层遗漏猎捕；
> **Prompt B** = M4 衔接层（`ControlPreAuthSession` 续跑 vs 新 `ControlAuthSession`）+ 本地审批机制边界。
> 立场：只问「我漏了什么 / 哪个决定是错的」，**不问**「Windows/.NET 实际行为是什么」——
> 后者本机实测可定案（项目硬规矩：模型回忆不得作为 HANDOFF 结论）。
> 状态：**已转发、已回收（2026-09-21）**——分流见
> `docs/M3_IMPLEMENTATION_REVIEW_TRIAGE.md`（两处缺陷级发现已采纳：transcript 拆分 / pre-auth 外层信封）。
> 前置事实：M3 已完工——574 自动化测试全绿 + 两机验收 PASS（2026-09-21）。
> 第一轮设计评审（M3 开工前）见 `docs/M3_REVIEW_TRIAGE.md`（已回收）；验收器 UI 评审与首启 UX 评审
> 各自已回收（`M3_ACCEPTANCE_UI_REVIEW_TRIAGE.md` / `UX_FIRST_RUN_REVIEW_TRIAGE.md`），均不在本文范围。

---

## Prompt A — M3 已实现传输层红队（可直接转发）

```text
You are reviewing the IMPLEMENTED networking layer ("M3") of a personal LAN
remote-desktop tool. M3 passed 574 automated tests and a real two-machine acceptance
run. I want criticism and omission-finding about what EXISTS — not a design review of
what might exist. Be concrete and skeptical.

=== CONTEXT ===

Product: Windows-only, LAN-only screen sharing / remote control between two PCs owned by
the same user. Personal use, not a commercial product.

Hard constraints (do NOT propose violating any of these):
- No cloud, no account, no CA/PKI, no relay, no UPnP/NAT traversal, no mDNS, no third-party service.
- IPv4 only. Peers must be RFC1918 AND in the same IPv4 subnet as the accepting local endpoint.
- Crypto allowlist: TLS, SHA-256, HMAC-SHA256, RandomNumberGenerator,
  CryptographicOperations.FixedTimeEquals, Windows DPAPI, BCL X509/SslStream only.
- No custom crypto. No "return true" certificate bypass. No option to disable pinning.

Stack: C#/.NET 10, x64. Dev/test machine: Windows 11 25H2 (build 26200). Windows 10 22H2
is a stated target but remains UNVERIFIED. Certificate/private-key storage strategy was
measured and settled in an earlier milestone — out of scope here.

=== THE BUILT SYSTEM (everything below is implemented and tested) ===

One control connection per session so far (video comes later). One TCP listener per
eligible local NIC, port 45873. Accept path order is deliberately fixed and enforced:
  accept -> same-subnet check (remote address vs the accepting listener's local address)
  -> admission limits -> TLS handshake -> pre-auth session.
  - same-subnet / admission / TLS-level failures close the connection SILENTLY on the host
    (the host currently has no logger; known, deferred — do not re-report).
  - admission limits: 8 concurrent globally, 2 per source IP; acquired after accept,
    released in finally. listen backlog is 8 and is explicitly NOT treated as a defense.
  - TLS server certificate: self-signed ECDSA P-256, 5 years, EKU serverAuth, KeyUsage
    digitalSignature only, non-CA. TLS 1.2 and 1.3 both allowed; TLS resumption and
    renegotiation explicitly disabled on both ends.
  - client-side pinning: SHA-256(cert.RawData) compared against the fingerprint learned
    from (unauthenticated) UDP discovery, with FixedTimeEquals. 10 local rejection codes:
    no-certificate, pin-mismatch, not-ecdsa-p256, is-ca, missing-key-usage,
    ku-without-digital-signature, missing-eku, eku-without-server-auth, not-yet-valid, expired.
    These codes are LOCAL-ONLY (logs/tests); no rejection reason is ever sent to the peer.

Framing: [4-byte big-endian uint32 length][UTF-8 JSON]. Length is validated in the uint
domain BEFORE any allocation: reject 0, reject over-cap. Caps: pre-auth single frame 4 KiB
(a constraint WE added beyond the spec — the spec's 1 MiB cap applies only after
authentication); post-auth control frames 1 MiB (64 KiB "nominal" warn threshold).
JSON parsing is strict: duplicate properties rejected, no comments, no trailing commas,
unmapped members rejected, nesting depth capped.

Pre-auth session (server side): reads exactly ONE frame, which must be channel_hello.
On success -> explicit PreAuthenticated state -> server sends close_notify (clean close).
The set of allowed operations while pre-authenticated is EMPTY, and a test fails if anyone
adds a capability before the access-key milestone. Local rejection codes on this path:
pre-auth-eof, pre-auth-frame, pre-auth-timeout.

FIVE ABSOLUTE STAGE DEADLINES (this is topic 1):
    TCP connect .............. 3 s
    TLS handshake ............ 5 s
    4-byte length prefix ..... 5 s
    whole frame payload ...... 10 s
    first channel_hello ...... 5 s
Each stage is timed from stage entry, is NEVER reset by bytes arriving, and is implemented
with its own CancellationTokenSource + CancelAfter. MEASURED: cancellation fires within
0-36 ms of the deadline, and in a real two-machine run a byte-dribbling peer was cut off
at 5013 ms / 5004 ms against the 5 s prefix deadline. NOTE WELL: we measured the ACCURACY
of the mechanism, NOT the appropriateness of the numbers — the five values are unreviewed
initial values. Attacking them is topic 1 below.

=== TOPIC 1 — ARE THESE VALUES RIGHT?  (3 / 5 / 5 / 10 / 5 seconds) ===

Context: same-subnet LAN only (typical RTT 0.1-2 ms; Wi-Fi can spike to 100-300 ms), two
ordinary desktop PCs; when a new session starts, several of these stages run back-to-back
on the same connection. The values must (a) not falsely kill legitimate connections on
busy/slow machines, (b) bound how long a hostile or broken peer can hold resources,
(c) work as ABSOLUTE per-stage deadlines.

1a. For each of the five stages, is the magnitude right? Consider: TLS handshake cost on a
    weak CPU; TCP retransmission behavior under transient packet loss (a too-tight connect
    cap can kill a connection that a retry would have completed — I can measure exact
    retransmission timing locally if the review needs it); first-connection JIT and
    warm-up effects; Wi-Fi power-save latency spikes.
1b. Payload is 10 s while prefix and hello are 5 s. Should they be equalized or inverted?
    With the global cap of 8 concurrent connections, can an attacker abuse the 10 s window
    in a way 5 s would not allow (e.g. cycling 8 sockets held for 10 s each)?
1c. Because the path is LAN-only, should any value be much smaller? State your reasoning per
    value. Flag any of the five you would NOT change, and why you would leave it alone.
1d. What is the smallest test or measurement that would catch a wrong value in either
    direction (false kill / resource abuse)?

Deliver: a recommended table {stage -> seconds -> rationale -> confidence}, plus the
counterargument for each change you propose (what breaks if you are wrong).

=== TOPIC 2 — FAILURE-MESSAGE TAXONOMY (what the PEER may learn vs what only WE may know) ===

Current behavior to review:
  - The server NEVER sends rejection reasons; failures close the connection (the exact
    wire signature per failure class is something I can measure locally — reviewed here is
    the POLICY: what may the peer distinguish, what must stay local).
  - Client-side pin failures surface as an exception whose LOCAL message embeds the short
    code, e.g. 'peer certificate failed validation (pin-mismatch)'. Nothing goes on the wire.
  - Measured TLS 1.3 oddity (documented, expected): when the CLIENT rejects the server
    certificate and sends its alert, the SERVER can consider the handshake complete before
    the alert arrives, then observes EOF and logs 'pre-auth-eof'. So for a client-side
    pin-mismatch, the server's own evidence is an INTERVAL (0 or 1 rows), never a fixed count.
  - Planned for the next milestone (access-key auth, specified but not yet built): all
    authentication failures collapse to ONE generic wire response; anti-brute-force per
    source IP: rolling 10-minute window, 5 failures -> 60 s rejection, random 300-800 ms
    delay added per failure, counters cleared on success, failures NOT logged by the limiter.

2a. Produce a classification table for EVERY failure class (subnet gate, admission limit,
    TLS-layer rejections incl. all 10 codes, framing violations, stage timeouts, pre-auth
    protocol violations, [next milestone] bad proof / approval denied): for each — what may
    the peer distinguish (design decision), what should the local operator see, what should
    the log contain, and why. Mark every place where "always generic to the peer" is WRONG
    or incomplete.
2b. Oracle analysis: with the above, what can an unauthenticated same-subnet attacker still
    learn or measure? Consider timing (jitter vs processing time), connection lifecycle
    differences, TLS alert types, retry pacing, and the TLS 1.3 'ghost row' above.
2c. The 300-800 ms uniform jitter: confirm or replace the distribution and values; state
    exactly which failure classes it should apply to; should pre-auth stage timeouts be
    jittered too? What does any jitter FAIL to hide?
2d. Should ANY failure become a specific, distinguishable error for the peer (e.g. 'wrong
    access key' vs 'unknown device')? Argue both sides; recommend one; give the exact wire
    strings if you choose specificity.

=== TOPIC 3 — HUNT FOR REMAINING GAPS IN THE BUILT SYSTEM ===

Known and accepted gaps — do NOT re-report: silent closes where the host has no logger
(deferred to the product milestone); the cross-subnet scenario cannot be reproduced in our
lab (unit-tested only); Windows 10 22H2 unverified; the operator acceptance tool and its UI
were reviewed separately.

Everything else is fair game. Areas most likely to yield something:
  - per-NIC listener lifecycle; partial startup degradation (one address fails to bind,
    others continue) — what run states can this leave the system in?
  - the admission limiter: global 8 / per-IP 2. The product will soon open TWO connections
    per session (control + video) plus possible retries while old connections drain — is
    per-IP 2 already too small? Is global 8 too big for two PCs?
  - the "exactly one hello, then clean close" pre-auth design: the NEXT milestone
    (access-key authentication) attaches here. What does this terminal state make awkward
    or unsafe later? What should be prepared NOW (test or code) so the change is a
    relocation, not a rework?
  - the empty-capabilities invariant as the ONLY guard between TLS and authentication —
    is a test over a static collection sufficient? What could regress silently?
  - framing: anything missing in length-validation order, partial reads, coalesced frames,
    cancellation/disposal races, EOF mid-prefix / mid-payload — give the missing tests.
  - shutdown path: 5 s budget (cancel + force + join) racing in-flight handshakes and
    handlers; what can leak or be miscounted.
  - evidence asymmetries: things the HOST can observe but the CLIENT cannot (or vice versa)
    for the same event — list the ones we must NOT paper over.

For each gap: what breaks, the concrete scenario, and the smallest test that catches it.
Rank: (1) could lead to a wrong SECURITY conclusion, (2) wrong UX/diagnostics, (3) cosmetic.

=== RULES FOR YOUR ANSWER ===
- Do not propose adding a CA, a trust store, a pinning-disable option, or any cloud/relay.
- Do not propose weakening: same-subnet check, RFC1918 rule, TLS, pinning, absolute deadlines.
- Do not present Windows/.NET behavior from memory as fact — label such statements.
- Label EVERY claim with exactly one of: VERIFIED KNOWLEDGE / NEEDS LOCAL EXPERIMENT / RECOLLECTION.
- Answer as numbered lists of concrete findings. For each: what is wrong, why it matters,
  and the smallest concrete change or test.

=== OUTPUT STRUCTURE (so I can triage mechanically) ===
  A. MUST-FIX NOW       -- real defects in the built system, before the next milestone.
  B. WORTH A TEST       -- behaviors worth pinning down with an explicit test/measurement.
  C. HINTS / UNVERIFIED -- things I must not record as fact until measured locally.
  D. VALUES & TAXONOMY  -- your recommended deadline table (topic 1) and the
                           peer-vs-local message classification table (topic 2).
```

---

## Prompt B — M4 阶段 0 决策输入（可随 A 一起转发；也可以只发 A）

```text
You are reviewing TWO DESIGN DECISIONS for the next milestone (access-key authentication)
of the same LAN remote-desktop tool, BEFORE any code is written. Give attack/failure
analysis and a recommendation, not reassurance. The same hard constraints apply as before
(no cloud/CA/relay; crypto allowlist; LAN-only, same-subnet; never weaken existing
controls), plus: minimal-change principle — we prefer designs that relocate existing
tested code over rewrites.

=== WHERE WE ARE ===
The transport milestone (TLS + certificate pinning + length-prefixed JSON framing) is
complete. On the server side a connection currently ends like this:
    TLS -> read exactly ONE frame (must be channel_hello) -> explicit "PreAuthenticated"
    state -> server closes cleanly (close_notify).
The milestone deliberately left NO placeholder for authentication — we did not park
unauthenticated sockets waiting for future code. One test
("PreAuthenticated_Allows_Nothing_Before_M4") fails if anyone adds any capability to the
pre-auth state before this milestone lands.

=== WHAT THE NEXT MILESTONE ADDS (from our spec; fixed) ===
- Access key: 128-bit random, stored DPAPI-protected at rest, transferred out of band,
  never sent on the wire (displayed as 26 Base32 characters). Both machines already have
  device identity (self-signed certificate + device id) from earlier milestones.
- Flow, on the SAME TLS connection: server sends challenge {sessionId, serverDeviceId,
  serverNonce(32B), certSha256, expiresInMs}; client responds {clientDeviceId, clientName,
  clientNonce(32B), requestedPermission, clientProof}; server then sends either
  {approval_pending} (if local approval is required) and/or {grantedPermission, serverProof,
  sessionToken(32B), videoAttachExpiresInMs}.
- clientProof = HMAC-SHA256(accessKeyBytes, canonical transcript);
  serverProof = HMAC-SHA256(accessKeyBytes, "server\0" || transcript).
- The transcript binds, byte-exactly: protocol version string, sessionId, both device ids,
  both nonces, both certificate fingerprints — including the fingerprint ACTUALLY PRESENTED
  on this connection (not merely the one from discovery) — and the requested/granted
  permission. Fixed field order, \0 separators, canonical base64/hex encodings; built by a
  pure function whose determinism is unit-tested.
- All authentication failures collapse to ONE generic wire error. Brute-force limiter per
  source IP: rolling 10-minute window, 5 failures -> 60 s rejection, 300-800 ms random
  delay per failure.
- Unknown controller requires local (host-side, human) approval by default. Approval happens
  ON the host machine. v1 open question: approve every time vs "remember for this runtime".
- sessionToken registered only AFTER successful authentication + approval.

=== DECISION 1 — where does the auth exchange live in code? ===
Options:
  (a) EXTEND the existing pre-auth session class so it continues into the auth exchange on
      the same connection (one class owns the accept side of the control channel end-to-end).
  (b) ADD a distinct auth-session layer; the pre-auth session, instead of closing, hands the
      still-open connection to it (explicit handover; each class keeps a single purpose).
  (c) something else — say what.

Constraints: the same TCP/TLS connection must continue (no reconnect — the client's proof
binds the fingerprint of the certificate presented on THIS connection; a reconnect would
change the transcript inputs); existing tests (notably the empty-capabilities test and the
clean-close terminal) will need conscious, justified rewrites wherever behavior legitimately
changes; the new structure must not open any path where a half-authenticated connection can
start doing work.

Analyze and recommend:
 1. Which option, with rationale? Which invariants/tests break or must be strengthened under
    each? Attack-surface differences between the options?
 2. While local approval is pending, the connection must wait — for how long, against what
    deadline model? All our existing deadlines are ABSOLUTE per stage, but a human approval
    is an unbounded wait. What should bound it (per-side timeout? who may abort?), and what
    should happen on approval-timeout vs explicit denial (wire + local)?
 3. Should the auth exchange get its OWN absolute deadline budget, distinct from the
    transport one? Propose values or a rule, and the simplest test that would catch a wrong
    choice.

=== DECISION 2 — boundary of the local-approval mechanism ===
Facts: the product UI does not exist yet; the current desktop app has ZERO wiring to the
transport layer; the transport layer is a library with unit tests; there is also a throwaway
operator acceptance tool. The approval mechanism must be testable NOW without prematurely
committing the product-UX shape (that decision belongs to the owner and is pending).
Options:
  (a) abstraction only: an approval-gate interface + default test double; no real UI anywhere yet;
  (b) abstraction + a minimal approval surface in the operator acceptance tool;
  (c) build the real product UI hook now.

Analyze and recommend:
 1. Which option, with rationale, and what design constraints must the abstraction carry NOW
    so that: (i) it can never be silently bypassed (no auto-approve default, no
    timeout-means-approve); (ii) a later UI attachment needs no rework; (iii) the remote peer
    cannot spoof or influence the approval decision in any flow; (iv) a future "remember this
    device" concept cannot weaken challenge-response for unknown devices.
 2. Attack the approval flow itself, from a malicious same-subnet peer's perspective:
    approval spam / operator fatigue; several simultaneous pending approvals and the operator
    approving the WRONG one; races between approval, denial, timeout and disconnect; anything
    that makes approval unobservable or forgeable. For each: the smallest mitigation and its cost.
 3. What is the minimal set of facts the approval prompt must show the operator so the
    operator approves the RIGHT peer — given that all discovery data (device name, address)
    is unauthenticated at that point?

=== RULES & OUTPUT (same as Prompt A) ===
- Label every claim: VERIFIED KNOWLEDGE / NEEDS LOCAL EXPERIMENT / RECOLLECTION.
- Do not propose violating any hard constraint; do not add cloud/CA/relay.
- Output: for each decision -> your recommendation, the reasoning, the strongest argument
  AGAINST your recommendation, and the smallest test that would falsify it.
```

---

## 转发之后怎么用

- 模型的输出**只用来发现问题，不用来定案**。任何「Windows/.NET 行为」类断言回到本机实测。
- 回收后先落 `docs/M3_IMPLEMENTATION_REVIEW_TRIAGE.md` 分类（接受 / 拒绝并给理由 / 待实测），
  再决定是否写进 HANDOFF / ADR。deadline 数值与消息分类表在落地前先做本机复核。
- Prompt B 两条决策的最终拍板 = **用户 + 本机事实**；模型回答只是决策输入。

---

# ⛔ 以下为**本机立场**，不要随 prompt 外发

- M3 两机验收已于 2026-09-21 完成（PASS，判定 = 两机证据配对）；本轮**不重开验收**，只做纸面红队。
- 五个 deadline 数值在评审回收前**不改代码**（等分类结论再动手）。
- 本机初步倾向（仅作基线，可被评审推翻）：
  - Decision 1 → (b) 显式交接（保住 M3 既有不变量与测试边界，双类单一职责）；
  - Decision 2 → (a) 抽象 + 测试替身（产品形态未拍板前不接线）。
- 评审若给出「Windows/.NET 行为」类论断，一律进 triage 的「待实测」桶，实测后才允许写进 HANDOFF。
