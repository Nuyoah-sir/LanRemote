# M3 验收器 UI — 外部设计评审 prompt（供转发给国外模型）

> 用途：M3 两机验收器窗口的**设计红队评审**。
> 立场：只问「我漏了什么 / 哪个决定是错的」，**不问**「Windows/.NET 实际行为是什么」——
> 后者本机实测可定案（项目硬规矩：模型回忆不得作为 HANDOFF 结论）。
> 状态：**待转发**。
>
> ⚠️ **正文是快照，不随代码更新**——它的价值是记录「评审者当时被告知了什么」。
> 其后验收器已经演进，正文里这几处**已过时**（按当前行为要核对请以 `HANDOFF.md`
> 与 `docs/M3_TWO_MACHINE_ACCEPTANCE.md` 为准）：
> - 必做场景从 3 个变 **4 个**（多了 `slow-dribble`，用来区分绝对 deadline 与可重置 deadline）；
> - 结论不再弹 MessageBox，改为窗口顶部**结论横幅**（免得挡住日志）；
> - 日志落盘不再是 `gui.log`：每轮一个不可变文件
>   `m3-<角色>-<UTC时刻>-<runId>.log`，`gui.log` 只留进程级事实（如 `[GUI] 窗口渲染完成`）。

---

## 背景：这次要评审什么

M3 的代码已经写完并通过 573 个自动化测试，但**两机真机验收还没做**。
验收需要一个「点几下就能把传输层真正跑起来」的入口。

我第一版做成了**控制台程序 + 批处理启动器**，被用户连纠三次：

1. 「你打包解压出来的，怎么没有启动exe」——控制台 exe 双击不带参数只打 usage 就退出，窗口一闪而过。
2. 「双击入口不应该就是一个exe可执行文件吗？为什么要用脚本？」
3. 「我们这个做的是软件啊，你全量阅读handoff了吗」

第 3 条成立：项目的既有约定是 M2 的两机验收就在**两台机器的 WPF 界面**上做的，
总原则是「终端用户永远不需要打开 PowerShell」。

现在改成 `WinExe` + WPF，双击 = 一个窗口。**我已经按自己的判断定稿并提交了**，
但我想知道这个窗口设计还漏了什么、哪些决定是错的、哪些"看起来方便"的交互其实会制造
**假通过**（vacuous pass）。这正是我最怕的东西，请重点攻击这一面。

```text
You are reviewing the UI design of a two-machine acceptance harness for a personal
LAN remote-desktop tool. I want criticism and omission-finding, not reassurance.
Be concrete and skeptical. Your primary target is anything that could let a BROKEN
implementation be recorded as a PASS, or a PASS be recorded when nothing was tested.

=== CONTEXT ===

Product: Windows-only, LAN-only screen sharing / remote control, personal use, two PCs
belonging to the same user. No cloud, no account, no CA/PKI, no relay, no NAT traversal.

Stack: C# / .NET 10 / WPF / x64, self-contained publish. Windows 10 22H2 / Windows 11.

Two INDEPENDENT TLS/TCP connections per session (Control :45873, Video later).
This milestone covers Control only.

What the milestone must prove, on real hardware:
  1. TLS handshake succeeds between the two machines.
  2. The peer is identified by CERTIFICATE PUBLIC-KEY/FINGERPRINT PINNING (SHA-256 of
     cert.RawData, uppercase hex, compared with FixedTimeEquals). There is no CA.
  3. A mismatching fingerprint must FAIL the handshake, and no application data may be
     accepted before it fails.
  4. A peer that completes TLS but never sends the first application frame must be cut off
     by an ABSOLUTE per-stage deadline (deadline is set when the stage is entered and is
     NEVER reset by bytes arriving; otherwise a peer sending one byte per (timeout - epsilon)
     keeps the connection alive forever).
  5. A peer whose source address is NOT in the same IPv4 subnet as the accepting listener
     address must be rejected BEFORE TLS. (Cannot be produced in this lab: Windows will not
     let you choose the source address. It is covered by unit tests only and is explicitly
     declared out of scope for this run.)
  6. After a successful pre-auth hello, the connection must reach an explicit
     PreAuthenticated state and then be CLOSED CLEANLY (close_notify), not reset.

IMPORTANT: this harness is NOT the product UI. The product UI does not exist yet. This is a
throwaway operator tool used by one person (the owner) on two machines, ONCE per milestone,
to produce evidence that gets pasted back into a handoff document.

=== THE HARNESS AS BUILT ===

It is a WinExe + WPF app named LanRemote.Acceptance.exe. Double-clicking it opens ONE window.
No scripts, no command line, no PowerShell. Previous revisions that shipped a console exe
plus a .cmd/.ps1 launcher were removed on purpose.

The window has four areas, top to bottom:

A. Identity / network panel (filled automatically on load from a self-check):
     deviceCode         e.g. M5WC-14GX   (Base32 of SHA256(deviceGuid), first 5 bytes;
                                          NOT a secret, safe to paste into chat)
     certSha256         64 hex chars, the fingerprint that gets pinned
     listenAddresses    the RFC1918 addresses this machine will bind
     status             either "ready to take part in two-machine acceptance" (green)
                        or a red explanation + the exact command to fix it
   If not ready, the two role buttons are DISABLED (so the operator cannot click into a
   state that silently does nothing).

B. Role panel. Two groups on the SAME window, because either machine may be either role:
     [This machine is the HOST (start listening)]  [Stop listening]
     Peer device code: [______]  [This machine is the CLIENT (run the 3 scenarios)]  [Abort]

C. Log area: read-only, monospaced, auto-scroll, contains everything.

D. Footer: [Copy all log] [Clear log] [Open log folder] + the log path.

Behaviour:
  - HOST: binds, starts discovery, loops accepting connections until the operator clicks Stop.
    For each connection it prints a per-connection result line and tracks counters
    (accepted / preAuthenticated / rejected), then prints one summary line on stop:
        [HOST] accepted=2 preAuthenticated=1 rejected=1 cleanStop=True
  - CLIENT: reads the peer code the operator typed, then runs THREE scenarios in sequence:
        success        expect handshake OK, pin match, hello accepted, clean close (eof)
        pin-mismatch   expect handshake REJECTED; the tool substitutes a different but
                       syntactically valid fingerprint
        timeout        complete TLS, send nothing, expect the server to cut us off at the
                       length-prefix deadline (~5s)
    Each scenario prints a batch of lines and one machine-checkable RESULT line, then the
    window shows a summary "x/3 scenarios behaved as expected" and a message box.
  - Logs are written BOTH to the UI and to %TEMP%\lanremote-m3-acceptance\gui.log, because a
    WinExe has no console and Console.WriteLine would be invisible.
  - Any unhandled exception is written to crash.log and shown in a message box.

Exit-code convention inherited from the earlier console version and translated to words:
    0 = behaved as expected
    1 = genuinely failed
    2 = PRECONDITION not met (nothing was tested)

=== KNOWN WEAKNESSES I ALREADY KNOW ABOUT (do not just repeat these) ===

  - The transport host currently has NO logger, so same-subnet rejection / admission
    rejection / TLS failure are all silent returns. During pin-mismatch the HOST shows
    nothing, which is expected. Evidence therefore leans on the CLIENT side.
  - One scenario (cross-subnet) cannot be produced on real hardware in this lab.
  - An earlier revision had a real vacuous-assertion bug: pin-mismatch treated ANY handshake
    failure as PASS, so a closed port / firewall drop / host-not-started would also "pass"
    while the subnet gate and pinning never executed. Fixed by adding an independent TCP
    reachability probe, and by deliberately NOT treating ConnectionReset as
    "nothing is listening" (accept-then-close is exactly an RST). If the probe fails, the
    scenario returns 2 instead of PASS. I want to know WHAT ELSE has this shape.

=== QUESTIONS ===

1. Where else in this UI can a broken implementation be recorded as a PASS, or can a PASS be
   recorded when nothing was actually tested? Enumerate concrete attack/failure paths, not
   generalities.

2. The CLIENT runs the three scenarios back-to-back against ONE host that was started once.
   Is sequencing them in a single click a good idea, or does it create cross-scenario
   contamination? Consider: shared discovery cache, shared TLS/credential state, the host's
   counters, and TCP TIME_WAIT on a fixed port. If they should be isolated, what is the
   cheapest isolation that still keeps the operator's work to "click once"?

3. The HOST prints one summary line with counters. What could make that summary line
   misleading even when every number is individually correct? (Think about what is NOT
   counted, and about ordering.)

4. I decided that the two roles live on ONE window with two button groups, rather than two
   modes or two separate windows. Is that the right call for a two-machine workflow where
   the operator is walking between two physical machines? What specifically goes wrong?

5. The readiness self-check DISABLES the role buttons when the machine has no qualifying
   RFC1918 NIC. Is disabling correct, or should the operator be allowed to try and see the
   failure? Which failure mode is worse?

6. What evidence should this UI be REQUIRED to produce so that a reviewer reading only a
   pasted log can decide "M3 passes"? What is it printing today that is not evidence, and
   what evidence is it not printing?

7. Anything about WPF/WinExe specifically that makes this harness untrustworthy as a
   measurement instrument? (Example: I hit a crash where InvariantGlobalization=true made the
   window die during Measure because WPF's font cache needs real CultureInfo. What else in
   this stack can silently change the measurement or hide a failure?)

Give me: (a) a ranked list of concrete defects and omissions, (b) for each, the specific
change you would make, (c) anything you would REMOVE because it creates false confidence.
Be blunt. I would rather delete half of this UI than ship a tool that can produce a
believable PASS for a broken build.
```

---

## 转发之后怎么用

- 模型的输出**只用来发现问题，不用来定案**。
- 任何「Windows/.NET 行为」类的断言，必须回到本机实测（项目硬规矩）。
- 结论先落 `docs/M3_REVIEW_TRIAGE.md` 做分类（接受 / 拒绝并给理由 / 待实测），
  再决定是否写进 HANDOFF。
