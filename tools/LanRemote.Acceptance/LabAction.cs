namespace LanRemote.Acceptance;

/// <summary>
/// 「准备本机」的固定动作集。<b>动词是封闭的</b>（ADR-035 的窄域提权模型）。
/// </summary>
/// <remarks>
/// <para>提升实例只认这三种动作，每一种都对应一个写死的参数组合——
/// 调用方<b>无法</b>把任意命令、任意脚本路径塞进提权那一步。
/// 新增第四种动作之前必须先改 ADR：提权边界不得靠实现方便而蔓延。</para>
/// </remarks>
internal enum LabAction
{
    /// <summary>写入 A 机 lab 地址（192.168.1.10）。</summary>
    ApplyA,

    /// <summary>写入 B 机 lab 地址（192.168.1.20）。</summary>
    ApplyB,

    /// <summary>撤销：删掉 lab 地址与两条入站规则，把网卡交还它原来的配置。</summary>
    Undo,
}

/// <summary>
/// <see cref="LabAction"/> 的文本映射，统一日志与命令行的说法。
/// </summary>
internal static class LabActionText
{
    /// <summary>headless 动词名：<c>lab-apply</c> / <c>lab-undo</c>。</summary>
    public static string Verb(this LabAction action) =>
        action == LabAction.Undo ? HeadlessCommand.RoleLabUndo : HeadlessCommand.RoleLabApply;

    /// <summary>脚本的 <c>-Role</c> 取值；撤销没有角色。</summary>
    public static string? LabRole(this LabAction action) => action switch
    {
        LabAction.ApplyA => "A",
        LabAction.ApplyB => "B",
        _ => null,
    };

    /// <summary>该动作要保证出现的 lab 地址；撤销返回 <see langword="null"/>。</summary>
    public static string? Address(this LabAction action) => action switch
    {
        LabAction.ApplyA => LabSetupRole.AddressA,
        LabAction.ApplyB => LabSetupRole.AddressB,
        _ => null,
    };

    /// <summary>中文短语，写进日志。</summary>
    public static string Describe(this LabAction action) => action switch
    {
        LabAction.ApplyA => "准备为 A 机",
        LabAction.ApplyB => "准备为 B 机",
        _ => "撤销 lab 网络设置",
    };
}
