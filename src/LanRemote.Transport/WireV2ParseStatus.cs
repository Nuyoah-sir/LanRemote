namespace LanRemote.Transport;

/// <summary>纯载荷分类，不是身份认证、连接结果或可兑换的父会话资格。</summary>
internal enum WireV2ParseStatus
{
    Invalid = 0,
    /// <summary>完整已知字段形状声明了其他正 Int32 版本；不保证认识未来的字段集。</summary>
    UnsupportedVersion,
    /// <summary>完整合法的 V1 载荷声明；仅诊断，绝不作为 V1 fallback 或旧软件身份认证。</summary>
    LegacyV1,
    /// <summary>V2 载荷合同通过；仍须实际 TLS/pin、proof、审批和状态机。</summary>
    AcceptedV2,
}
