using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LanRemote.Core.Models;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 控制端角色：发现 → 冻结快照 → TLS → hello，并按场景判定 PASS/FAIL。
/// </summary>
internal static class ClientRole
{
    /// <summary>场景：一切正常，hello 应当被接受。</summary>
    public const string ScenarioSuccess = "success";

    /// <summary>场景：指纹不符，握手必须失败。</summary>
    public const string ScenarioPinMismatch = "pin-mismatch";

    /// <summary>场景：连上不发 hello，服务端必须按时限切断。</summary>
    public const string ScenarioTimeout = "timeout";

    /// <summary>场景：跨子网，服务端必须在同子网闸门上拒掉（发现不到，所以直连地址）。</summary>
    public const string ScenarioCrossSubnet = "cross-subnet";

    private static readonly TimeSpan DiscoverBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 跑 client 角色。
    /// </summary>
    /// <param name="scenario">场景名。</param>
    /// <param name="deviceCode">对端设备码（发现类场景用）。</param>
    /// <param name="address">对端地址（跨子网场景用）。</param>
    /// <param name="pinHex">对端指纹（跨子网场景用）。</param>
    /// <param name="port">对端 TLS 端口。</param>
    /// <param name="cancellationToken">Ctrl+C。</param>
    /// <returns>0 = 场景表现符合预期；1 = 不符合预期；2 = 前置条件不满足。</returns>
    public static async Task<int> RunAsync(
        string scenario,
        string? deviceCode,
        string? address,
        string? pinHex,
        int port,
        CancellationToken cancellationToken)
    {
        using AcceptanceContext context = new(LogLevel.Warning);
        await context.InitializeAsync(cancellationToken);

        Console.WriteLine($"[CLIENT] scenario    = {scenario}");
        Console.WriteLine($"[CLIENT] deviceCode  = {context.Identity.DeviceCode}");
        Console.WriteLine($"[CLIENT] certSha256  = {context.Certificate.Sha256FingerprintHex}");

        ConnectionTarget target;

        if (scenario == ScenarioCrossSubnet)
        {
            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(pinHex))
            {
                Console.WriteLine("[CLIENT][RESULT] outcome=FAIL reason=missing --address/--pin");
                return 2;
            }

            if (!ConnectionTarget.TryCreate(
                    CrossSubnetPeerId,
                    IPAddress.Parse(address),
                    port,
                    pinHex,
                    out ConnectionTarget? direct) || direct is null)
            {
                Console.WriteLine("[CLIENT][RESULT] outcome=FAIL reason=bad-target");
                return 2;
            }

            target = direct;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                Console.WriteLine("[CLIENT][RESULT] outcome=FAIL reason=missing --device-code");
                return 2;
            }

            await context.StartDiscoveryAsync(cancellationToken);

            DiscoveredDevice? peer = await WaitForPeerAsync(context, deviceCode, cancellationToken);
            if (peer is null)
            {
                Console.WriteLine($"[CLIENT][RESULT] outcome=FAIL reason=peer-not-found deviceCode={deviceCode}");
                return 2;
            }

            Console.WriteLine(
                $"[CLIENT] peer        = {peer.DeviceCode} {peer.Address}:{peer.Port} pin={peer.CertificateSha256}");

            if (!ConnectionTarget.TryCreate(peer, out ConnectionTarget? frozen) || frozen is null)
            {
                Console.WriteLine("[CLIENT][RESULT] outcome=FAIL reason=snapshot-rejected");
                return 2;
            }

            target = scenario == ScenarioPinMismatch
                ? RebuildWithWrongPin(peer)
                : frozen;
        }

        return await ExecuteAsync(scenario, target, cancellationToken);
    }

    private static async Task<int> ExecuteAsync(
        string scenario,
        ConnectionTarget target,
        CancellationToken cancellationToken)
    {
        // 「失败即 PASS」的两个场景必须先证明对端端口是开着的。
        //
        // 少了这一步，判定是空洞的：host 没启动 / 防火墙拦掉 / 地址写错，
        // 都会让握手失败，于是 pin-mismatch 与 cross-subnet 统统报 PASS——
        // 而实际上同子网闸门和 pinning 一行都没被执行到。
        // 前置条件不满足要报 2（环境没配好），绝不能报 0（符合预期）。
        if (scenario is ScenarioPinMismatch or ScenarioCrossSubnet)
        {
            bool open = await ProbeTcpAsync(target.RemoteAddress, target.Port, cancellationToken);
            Console.WriteLine($"[CLIENT] tcpProbe     = {(open ? "open" : "unreachable")} " +
                              $"{target.RemoteAddress}:{target.Port}");

            if (!open)
            {
                Console.WriteLine(
                    $"[CLIENT][RESULT] outcome=FAIL reason=peer-port-unreachable " +
                    $"address={target.RemoteAddress}:{target.Port} " +
                    $"// 对端 TLS 端口连不上，无法判定「被拒绝」；请先在对端跑 host 角色");
                return 2;
            }
        }

        TlsClientConnector connector = new();

        // ① 握手
        TlsConnection connection;
        try
        {
            connection = await connector.ConnectAsync(target, null, null, cancellationToken);
        }
        catch (Exception ex)
        {
            string type = ex.GetType().FullName ?? ex.GetType().Name;
            Console.WriteLine($"[CLIENT] handshake failed: {type}: {ex.Message}");

            // 连都没连上 ≠ 被拒绝。这属于前置条件不满足，不是 PASS。
            if (LooksLikeNothingListening(ex))
            {
                Console.WriteLine(
                    $"[CLIENT][RESULT] outcome=FAIL reason=peer-port-unreachable " +
                    $"handshake={type} // TCP 层就没连上，同闸门/pinning 未被触及");
                return 2;
            }

            // 只有「指纹不符」与「跨子网」这两个场景，握手失败才是 PASS。
            bool expected =
                scenario is ScenarioPinMismatch or ScenarioCrossSubnet;

            return Report(
                expected,
                expected
                    ? "握手按预期被拒绝"
                    : "本该握手成功却失败了",
                ("handshake", type));
        }

        using (connection)
        {
            Console.WriteLine(
                $"[CLIENT] tls ok: proto={connection.NegotiatedProtocol} " +
                $"presentedPin={Convert.ToHexString(connection.Identity.PresentedCertSha256.Span)}");

            // ② 按场景说话（或故意不说）
            if (scenario == ScenarioTimeout)
            {
                Console.WriteLine("[CLIENT] 故意不发 hello，等服务端按 pre-auth 时限切断……");
            }
            else
            {
                await FrameWriter.WriteHelloAsync(
                    connection.Stream,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                Console.WriteLine("[CLIENT] hello sent");
            }

            // ③ 读回来：服务端收尾后这里必然是 EOF 或异常。
            CloseObservation close = await WaitForPeerCloseAsync(connection, cancellationToken);
            Console.WriteLine($"[CLIENT] readBack    = {close.Kind} // {close.Detail}");

            return scenario switch
            {
                ScenarioTimeout => Report(
                    close.Closed,
                    close.Closed
                        ? $"服务端在 pre-auth 时限内切断（{close.Kind}）"
                        : "服务端没有按时限切断",
                    ("peerClosed", close.Kind)),

                ScenarioSuccess => Report(
                    close.Closed,
                    close.Closed
                        ? $"hello 已被接受、服务端干净关闭（{close.Kind}）"
                        : "服务端没有收尾",
                    ("peerClosed", close.Kind)),

                // 走到这里说明握手竟然成功了——指纹不符/跨子网都不该如此。
                _ => Report(false, "本该被拒绝却握手成功", ("handshake", "succeeded")),
            };
        }
    }

    /// <summary>
    /// 把「对端怎么收的尾」如实记下来，而不是压成一个布尔。
    /// </summary>
    /// <remarks>
    /// 原来这里 <c>catch → return true</c>，任何异常都算「被关了」，
    /// 于是「读超时」和「被切断」在输出里长得一模一样，判定又是空洞的。
    /// 现在区分三种：EOF（干净关闭）/ RST 或异常（被切断）/ 超时（还挂着）。
    /// </remarks>
    private static async Task<CloseObservation> WaitForPeerCloseAsync(
        TlsConnection connection,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16];
        try
        {
            int read = await connection.Stream
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .AsTask()
                .WaitAsync(ReadBudget, cancellationToken);

            return read == 0
                ? new CloseObservation(true, "eof", "对端发了 close_notify，连接干净关闭")
                : new CloseObservation(
                    false,
                    "unexpected-data",
                    $"hello 之后对端还发了 {read} 字节——M3 终态不该有后续数据");
        }
        catch (TimeoutException)
        {
            // WaitAsync 到点：连接还挂着，对端没有收尾。
            return new CloseObservation(false, "still-open", "读预算内对端没有关闭连接");
        }
        catch (Exception ex)
        {
            // RST / 连接被重置同样算「被切断」，但要把异常类型留在证据里。
            string type = ex.GetType().FullName ?? ex.GetType().Name;
            return new CloseObservation(true, "reset", $"{type}: {ex.Message}");
        }
    }

    private readonly record struct CloseObservation(bool Closed, string Kind, string Detail);

    /// <summary>
    /// 纯 TCP 探针：只回答「对端 TLS 端口有没有人监听」。
    /// </summary>
    /// <remarks>
    /// 只看得见「连上 / 连不上」，不碰 TLS。它把「没开端口」与「开了端口但拒绝」
    /// 区分开——前者是环境问题，后者才是被测的安全判定。
    /// </remarks>
    private static async Task<bool> ProbeTcpAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using TcpClient probe = new(address.AddressFamily);
        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        budget.CancelAfter(ProbeBudget);

        try
        {
            await probe.ConnectAsync(address, port, budget.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断这个失败是不是「TCP 层根本没连上」。
    /// </summary>
    /// <remarks>
    /// 与真正的拒绝区分：<c>TransportHost</c> 的顺序是 accept → 同子网 → 准入 → TLS，
    /// 同子网闸门是在<b>接受之后</b>才关掉连接的，所以客户端看到的是 EOF / RST，
    /// 不是 <c>ConnectionRefused</c>。这里刻意<b>不</b>把 <c>ConnectionReset</c> 算进来，
    /// 因为对端 accept 后立刻关闭正是会走 RST。
    /// </remarks>
    private static bool LooksLikeNothingListening(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.NetworkDown
                    or SocketError.TimedOut)
            {
                return true;
            }
        }

        return false;
    }

    private static int Report(bool pass, string detail, params (string Key, string Value)[] fields)
    {
        string joined = string.Join(
            " ",
            fields.Select(field => $"{field.Key}={field.Value}"));

        Console.WriteLine($"[CLIENT][RESULT] outcome={(pass ? "PASS" : "FAIL")} {joined} // {detail}");
        return pass ? 0 : 1;
    }

    private static async Task<DiscoveredDevice?> WaitForPeerAsync(
        AcceptanceContext context,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        string normalized = deviceCode.Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DiscoverBudget);

        try
        {
            await foreach (DiscoveredDevice device in context.Discovery
                               .WatchAsync(budget.Token)
                               .WithCancellation(budget.Token))
            {
                string candidate = device.DeviceCode.Replace("-", string.Empty, StringComparison.Ordinal)
                    .ToUpperInvariant();

                if (string.Equals(candidate, normalized, StringComparison.Ordinal))
                {
                    return device;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>
    /// 把期望指纹改成另一个合法但不同的 64 位十六进制串。
    /// </summary>
    private static ConnectionTarget RebuildWithWrongPin(DiscoveredDevice peer)
    {
        string wrong = new string('0', 63) + "1";
        if (string.Equals(wrong, peer.CertificateSha256, StringComparison.OrdinalIgnoreCase))
        {
            wrong = new string('F', 64);
        }

        Console.WriteLine($"[CLIENT] 期望指纹被替换为 {wrong}（原值 {peer.CertificateSha256}）");

        bool created = ConnectionTarget.TryCreate(
            peer.DeviceId,
            peer.Address,
            peer.Port,
            wrong,
            out ConnectionTarget? target);

        if (!created || target is null)
        {
            throw new InvalidOperationException("构造错误指纹的快照失败——这是测试器自身的问题。");
        }

        return target;
    }

    /// <summary>
    /// 跨子网场景里对端的 deviceId 未经验证（发现不到它），这里填一个占位值——
    /// 它不参与任何安全判定，pin 与同子网校验都在它之外。
    /// </summary>
    private static readonly Guid CrossSubnetPeerId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>给手册用的场景清单。</summary>
    public static string UsableScenarios() => string.Join(" | ", new[]
    {
        ScenarioSuccess,
        ScenarioPinMismatch,
        ScenarioTimeout,
        ScenarioCrossSubnet,
    });
}
