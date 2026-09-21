using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// M3 两机验收器入口。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：<c>LanRemote.App</c> 目前只是<b>引用</b>了
/// <c>LanRemote.Transport</c>，并没有调用它。不借助这个工具，两台机器上的验收
/// 只能证明 discovery 还能用，碰不到 TLS / pin / hello 的任何一行。</para>
/// <para>退出码：0 = 符合预期；1 = 不符合预期；2 = 前置条件不满足（环境没配好）。</para>
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource ctrlC = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ctrlC.Cancel();
        };

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => await InfoRole.RunAsync(ctrlC.Token),

                "host" => await HostRole.RunAsync(
                    ReadInt(args, "seconds", 120),
                    ctrlC.Token),

                "client" => await ClientRole.RunAsync(
                    scenario: ReadString(args, "scenario", ClientRole.ScenarioSuccess),
                    deviceCode: FindString(args, "device-code"),
                    address: FindString(args, "address"),
                    pinHex: FindString(args, "pin"),
                    port: ReadInt(args, "port", 45873),
                    cancellationToken: ctrlC.Token),

                _ => PrintUsageAndFail(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex.GetType().FullName}: {ex.Message}");
            return 1;
        }
    }

    private static int PrintUsageAndFail(string unknown)
    {
        Console.WriteLine($"[FATAL] 未知子命令：{unknown}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            LanRemote M3 两机验收器

              LanRemote.Acceptance info
                  环境自检：身份 / 证书指纹 / 合格网卡 / 监听地址。
                  两台机器开跑之前都先各跑一次。

              LanRemote.Acceptance host [--seconds 120]
                  被控端：起 discovery + TLS Host，对每条连接跑完整 pre-auth 会话。

              LanRemote.Acceptance client --scenario <s> [选项]
                  s = success | pin-mismatch | timeout | cross-subnet

                  success / pin-mismatch / timeout 需要： --device-code XXXX-XXXX
                  cross-subnet 需要：                     --address <ip> --pin <64hex>

            退出码：0 = 符合预期，1 = 不符合预期，2 = 前置条件不满足。
            """);
    }

    private static string ReadString(string[] args, string name, string fallback)
    {
        string? value = FindString(args, name);
        return value ?? fallback;
    }

    private static string? FindString(string[] args, string name)
    {
        string flag = "--" + name;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string? raw = FindString(args, name);
        return raw is not null && int.TryParse(raw, out int value) ? value : fallback;
    }
}
