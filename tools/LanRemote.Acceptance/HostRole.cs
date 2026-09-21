using System.Net;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 被控端角色：真实网卡上起 TLS Host，对每条连接跑完整的 pre-auth 会话。
/// </summary>
internal static class HostRole
{
    /// <summary>
    /// 跑 host 角色。
    /// </summary>
    /// <param name="seconds">监听多久后自行停机。</param>
    /// <param name="cancellationToken">Ctrl+C。</param>
    /// <returns>进程退出码。</returns>
    public static async Task<int> RunAsync(int seconds, CancellationToken cancellationToken)
    {
        using AcceptanceContext context = new(LogLevel.Information);
        await context.InitializeAsync(cancellationToken);

        IReadOnlyList<IPAddress> addresses = context.ListenAddresses();

        Console.WriteLine("[HOST] deviceCode    = " + context.Identity.DeviceCode);
        Console.WriteLine("[HOST] deviceId      = " + context.Identity.DeviceId);
        Console.WriteLine("[HOST] certSha256    = " + context.Certificate.Sha256FingerprintHex);
        Console.WriteLine("[HOST] listenPort    = " + context.Config.TransportPort);
        Console.WriteLine("[HOST] listenAddress = " +
            (addresses.Count == 0 ? "(none — 本机没有合格 RFC1918 网卡)" : string.Join(", ", addresses)));

        if (addresses.Count == 0)
        {
            Console.WriteLine("[HOST][RESULT] outcome=FAIL reason=no-qualified-nic");
            Console.WriteLine("[HOST] 提示：先用 set-lab-ip.ps1 把两台机器放到同一个 192.168.1.0/24。");
            return 2;
        }

        await context.StartDiscoveryAsync(cancellationToken);

        int accepted = 0;
        int authenticated = 0;
        int rejected = 0;

        TransportTimeouts timeouts = BuildTimeouts();

        TransportHost host = new(
            addresses,
            context.SubnetPolicy,
            context.Certificate.Certificate,
            async (connection, token) =>
            {
                int index = Interlocked.Increment(ref accepted);
                ControlPreAuthSession session = new();

                ControlPreAuthResult result =
                    await session.RunAsync(connection, timeouts, token);

                Console.WriteLine(
                    $"[HOST] conn#{index} peer={connection.RemoteAddress} " +
                    $"proto={connection.NegotiatedProtocol} " +
                    $"state={result.State} rejection={result.Rejection ?? "-"}");

                // 可机器判定的那一行。
                Console.WriteLine(
                    $"[HOST][RESULT] conn#{index} peer={connection.RemoteAddress} " +
                    $"outcome={(result.Completed ? "PreAuthenticated" : "Rejected")} " +
                    $"rejection={result.Rejection ?? "-"}");

                if (result.Completed)
                {
                    Interlocked.Increment(ref authenticated);
                }
                else
                {
                    Interlocked.Increment(ref rejected);
                }
            },
            new TransportHostOptions
            {
                Port = context.Config.TransportPort,
                Timeouts = timeouts,
            });

        await using (host)
        {
            TransportHostStartResult start = host.Start();

            if (!start.IsListening)
            {
                Console.WriteLine("[HOST][RESULT] outcome=FAIL reason=no-listener");
                foreach (TransportHostBindFailure failure in start.Failures)
                {
                    Console.WriteLine($"[HOST] bind 失败 {failure.Address}: {failure.Error} {failure.Message}");
                }

                return 2;
            }

            Console.WriteLine("[HOST] listening   = " + string.Join(", ", start.BoundAddresses));
            Console.WriteLine($"[HOST] 等待连接 {seconds} 秒（Ctrl+C 提前结束）……");

            using CancellationTokenSource window =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(TimeSpan.FromSeconds(seconds));

            try
            {
                await Task.Delay(Timeout.Infinite, window.Token);
            }
            catch (OperationCanceledException)
            {
            }

            bool clean = await host.StopAsync(TimeSpan.FromSeconds(5));

            Console.WriteLine($"[HOST] accepted={accepted} preAuthenticated={authenticated} rejected={rejected} cleanStop={clean}");

            using CancellationTokenSource stopBudget = new(TimeSpan.FromSeconds(3));
            await context.Discovery.StopAsync(stopBudget.Token);
        }

        return 0;
    }

    private static TransportTimeouts BuildTimeouts() =>
        new(
            connectTimeout: TimeSpan.FromSeconds(3),
            handshakeTimeout: TimeSpan.FromSeconds(5),
            lengthPrefixTimeout: TimeSpan.FromSeconds(5),
            payloadTimeout: TimeSpan.FromSeconds(10),
            helloTimeout: TimeSpan.FromSeconds(5));
}
