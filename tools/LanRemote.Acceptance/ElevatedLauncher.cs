namespace LanRemote.Acceptance;

/// <summary>旧提权入口仅返回拒绝结果，不调用 shell 或 UAC。</summary>
internal static class ElevatedLauncher
{
    public sealed record LaunchResult(bool Started, bool Cancelled, int ExitCode, string? Error);

    public static Task<LaunchResult> RunElevatedAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout) =>
        Task.FromResult(new LaunchResult(false, false, (int)AcceptanceOutcome.HarnessError,
            LabSetupRole.DisabledMessage));
}
