# 首启体验 / 「零命令行」设计红队评审 prompt（供转发给国外模型）

> 用途：针对**首次运行体验**（first-run）与「一键化配置」的设计红队评审。
> 立场：只谈**设计取舍与安全模型**，**不问**具体 Windows/.NET API 行为——后者一律本机实测，
> 模型回忆不得写进 HANDOFF（项目硬规矩：严禁伪结论）。
> 触发来源：用户在 B 机上的原话——「我就不能以管理员模式打开，软件自动帮我处理该处理的事吗？
> 这个软件就应该运行之后就能看到在线的设备，这样设计才对啊。」
> 状态：**已转发并回收**（外部模型 Q1~Q6 全答 → `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md`；ADR-035/036 等已落地）。
>
> 相关的既有决策：ADR-024（网络诊断进 UI）、ADR-025（防火墙一键）、ADR-026（临时私有地址一键）。

---

## Prompt — first-run UX 红队评审（可直接转发）

```text
You are reviewing the first-run / setup experience of a personal LAN remote-desktop tool.
I want criticism and omission-finding, not reassurance. Be concrete and skeptical.

=== CONTEXT ===

Product: Windows-only, LAN-only screen sharing / remote control between two PCs owned by
the same user. Personal use, not a commercial product.

Hard constraints (do NOT propose violating any of these):
- No cloud, no account, no relay, no UPnP/NAT traversal, no third-party service, no mDNS.
- IPv4 only. Peers must be RFC1918 (10/8, 172.16-172.31, 192.168/16) AND on the same
  IPv4 subnet as the local endpoint used for the connection.
- No always-on Windows service, no kernel driver, no long-lived elevated background agent.
- Never silently change the host's network configuration or firewall state. (Automation
  has twice taken this development machine off the network; the rule is absolute.)
- Crypto allowlist: TLS, SHA-256, HMAC-SHA256, RandomNumberGenerator,
  CryptographicOperations.FixedTimeEquals, Windows DPAPI, BCL X509/SslStream only.

Stack: C# / .NET 10 / WPF, self-contained x64 publish, no installer yet — the user
unzips a folder and double-clicks an exe. Both machines run the same exe.
Discovery already works (UDP 45872, multicast + per-NIC directed broadcast; the UI shows
a device list with a refresh button). This review is about everything that must happen
BEFORE that device list is useful.

=== THE USER'S COMPLAINT (verbatim, translated) ===

"Why do I have to open Administrator mode and type commands? Can't the software just
handle what needs handling by itself? This software should show me the online devices as
soon as it runs — that's the correct design."

=== WHAT ACTUALLY HAPPENED ===

- The two acceptance machines sit on 172.100.166.x. 172.100.x.x LOOKS private but is NOT
  (the 172 block only covers 172.16-172.31), so the product CORRECTLY refuses to use that
  NIC and reports "no eligible private IPv4 NIC" plus a pointer to the lab-IP helper.
- For acceptance, the package ships a helper script set-lab-ip.ps1 that (a) appends
  192.168.1.10 / .20 to the real NIC while keeping the existing address (no outage),
  (b) sets the network profile to Private, (c) creates two inbound firewall rules
  (UDP 45872 discovery, TCP 45873 control). It needs elevation and is run by hand from an
  admin PowerShell. It is acceptance material, not the product.
- The user's reaction was about the PRODUCT: they consider "open an admin shell and type
  commands" unacceptable for a piece of software they run.

=== WHAT WE ALREADY DECIDED (our own product principles / ADRs) ===

- Terminal users never open PowerShell. "One-click" means: invisible entry point +
  explicit trigger + UAC consent. Never silent automation.
- ADR-024: network diagnostics must surface INSIDE the UI ("why can't I see the other
  PC?"), naming the reason, the adapter, and the actual address.
- ADR-025: an in-app "firewall one-click" that only allows LocalSubnet and can be revoked
  precisely.
- ADR-026: an in-app "temporary private lab address" one-click (not before the firewall
  one-click, excludes virtual adapters, idempotent undo).

=== QUESTIONS (answer each; where you disagree with the ADRs, say so explicitly) ===

Q1. Which parts of first-run setup can NEVER be automated away on Windows — i.e. always
    require the user's explicit consent and/or an elevated prompt — and which parts can be
    reduced to "one UAC click"? Separate [OS security model facts] from [app-level
    choices]. Do not guess API details; reason about the security model.
Q2. The device list needs inbound UDP (discovery replies / multicast) to be useful on the
    controlled side, and the controlled side also needs inbound TCP. What is the least
    intrusive, safest shape for "make this machine reachable" in a personal tool?
    Compare at least three shapes, e.g. (a) rely on Windows' own first-listen firewall
    prompt, (b) in-app one-click that adds a scoped rule, (c) instruct the user manually
    with screenshots. State which you would pick and why.
Q3. For the "both machines are on a non-RFC1918 subnet" case — a real, non-hypothetical
    situation here — should the product (a) only diagnose and refuse, (b) offer a
    temporary lab address one-click, (c) something else? Give trade-offs and a
    recommendation, respecting the hard constraints.
Q4. "It should just show me the online devices when it runs." Under what preconditions is
    that true, and which of those preconditions are OS/network facts that no application
    can eliminate? What is the honest UI copy for the cases where it cannot be true?
Q5. Red-team our own one-click plan (ADR-025/026): give at least three concrete ways it
    can go wrong on a real user's machine (including the user's own machine, where
    automation already caused two outages), and the guardrails that must exist.
Q6. Anything important we have not asked? In particular: which of these steps is a
    one-time setup vs every-run friction, and does our UI communicate that distinction?

=== OUTPUT FORMAT ===

- Numbered answers matching the questions. No boilerplate, no restating the context.
- Mark each claim as [FACT: Windows security model] / [OPINION/TRADE-OFF] /
  [UNSURE — NEEDS LOCAL EXPERIMENT]. Guessing is fine; hiding that you are guessing is not —
  every Windows-behaviour claim gets verified locally anyway.
- If you think one of the hard constraints makes the product worse for this user, say it
  plainly — we would rather hear it now than after shipping.
```

---

## 转发后怎么处理结论

1. 模型结论**不得直接写进 HANDOFF**（项目硬规矩）。先落到
   `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md`（逐条：接受 / 驳回 / 待实测）。
2. 凡是「Windows 实际会怎样」的断言 → 一律转成本机可执行的小实验，测完再下结论。
3. 与现有 ADR 冲突的结论 → 要么改 ADR（写清理由），要么明确驳回。
