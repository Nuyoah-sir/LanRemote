using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

// 仅测试使用：合成同会话的目录约定值，不执行真实收发、TLS 握手、审批或认证。
internal static class V2AgreementFixture
{
    internal static DisplayCatalogAgreementBinding Create(Guid sessionId)
    {
        Assert.True(DisplayCatalogAgreementFrame.TryParseOffer(
            DisplayCatalogAgreementFrame.SerializeOffer(sessionId), sessionId, out var offer, out _));
        Assert.True(DisplayCatalogAgreementFrame.TryParseAccept(
            DisplayCatalogAgreementFrame.SerializeAccept(sessionId), sessionId, out var accept, out _));
        return DisplayCatalogAgreementBinding.Create(sessionId, offer!, accept!);
    }
}
