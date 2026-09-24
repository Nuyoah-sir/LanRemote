namespace LanRemote.Acceptance;

/// <summary>仅用于识别并拒绝历史改网动作；所有值均不可执行。</summary>
internal enum LabAction
{
    ApplyA,
    ApplyB,
    Undo,
}
