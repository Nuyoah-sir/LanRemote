namespace LanRemote.Transport;

internal enum VideoAttachStatus
{
    Attached,
    Cancelled,
    // 初次查询尚无条目（也包括已注销）；允许路由在固定短预算内再查。
    NotRegistered,
    // 本次已观察条目在最终检查失效；必须终止，不允许重试跨越 ABA。
    Unavailable,
    Expired,
    AlreadyAttached,
    InvalidProof,
    InvalidInput,
    IdentityMismatch
}
