namespace LanRemote.Transport.Auth;

/// <summary>已通过纯载荷校验的双向目录约定；不是认证、审批或一次性会话凭据。</summary>
/// <remarks>
/// profile/schema 已由封闭的 DisplayCatalogAgreementFrame 解析器限制为 2/1。
/// Create 仅绑定调用方预期的 challenge session 与两方向值，不证明真实收发、顺序或新鲜性。
/// </remarks>
internal sealed class DisplayCatalogAgreementBinding
{
    private DisplayCatalogAgreementBinding(Guid sessionId) => SessionId = sessionId;

    internal Guid SessionId { get; }

    internal static DisplayCatalogAgreementBinding Create(Guid expectedSessionId,
        DisplayCatalogAgreementFrame offer, DisplayCatalogAgreementFrame accept)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(accept);
        if (expectedSessionId == Guid.Empty)
            throw new ArgumentException("预期会话不可为空。", nameof(expectedSessionId));
        if (!offer.IsOffer || accept.IsOffer)
            throw new ArgumentException("目录约定方向不正确。");
        if (offer.SessionId != expectedSessionId || accept.SessionId != expectedSessionId)
            throw new ArgumentException("目录约定会话不匹配。");
        return new DisplayCatalogAgreementBinding(expectedSessionId);
    }
}
