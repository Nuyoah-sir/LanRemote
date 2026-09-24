namespace LanRemote.Acceptance;

/// <summary>旧提升实例入口始终拒绝；不检查权限，也不存在脚本执行路径。</summary>
internal static class LabWorkerRole
{
    public static Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        LabAction action,
        CancellationToken cancellationToken) =>
        LabSetupRole.PrepareAsync(run, action, cancellationToken);
}
