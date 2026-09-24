namespace LanRemote.Acceptance;

/// <summary>旧改网入口仅保留明确拒绝，不解析脚本、不启动进程、不请求提权。</summary>
internal static class LabSetupRole
{
    public const string DisabledReason = "network-configuration-disabled";
    public const string DisabledMessage =
        "已禁用网络配置修改及撤销：验收器只读检查现有网络，不改 DHCP/IP/DNS/路由/热点/ICS/网络类别/防火墙。" +
        "管理员权限、elevated 参数或 UAC 确认均不能豁免；不会执行旧脚本。";

    public static Task<AcceptanceOutcome> PrepareAsync(
        AcceptanceRun run,
        LabAction action,
        CancellationToken cancellationToken)
    {
        run.Log.WriteLine($"[LAB][RESULT] outcome=HARNESS_ERROR reason={DisabledReason} // {DisabledMessage}");
        return Task.FromResult(run.Complete(AcceptanceOutcome.HarnessError, DisabledMessage));
    }
}
