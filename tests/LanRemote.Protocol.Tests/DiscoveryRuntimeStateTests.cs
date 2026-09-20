using LanRemote.Core.Configuration;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 运行时状态与「允许被发现」的决策语义。
/// </summary>
/// <remarks>
/// §53 要求把「是否应当 announce / 是否应当回应 probe」做成可测试逻辑。
/// 注意：「扫描别人」不受 AllowDiscovery 控制，这一点由 LanDiscoveryService 实现，
/// 无法在无 socket 的单元测试里覆盖，已在 HANDOFF 记录。
/// </remarks>
public sealed class DiscoveryRuntimeStateTests
{
    private static DeviceIdentity Identity(Guid id) => new(
        id,
        Core.Identity.DeviceCode.Derive(id),
        "LOCAL-PC",
        new string('A', 64));

    [Fact]
    public void BeforeInitialize_NothingIsExposed()
    {
        DiscoveryRuntimeState state = new();

        Assert.False(state.IsInitialized);
        Assert.False(state.TryGetCurrent(out DiscoveryRuntimeSnapshot? snapshot));
        Assert.Null(snapshot);

        // 这是隐私保证：身份没加载好之前，discovery 拿不到任何可广播的数据。
        Assert.Throws<InvalidOperationException>(() => state.Current);
    }

    [Fact]
    public void Initialize_CapturesIdentityAndConfig()
    {
        DiscoveryRuntimeState state = new();
        Guid id = Guid.NewGuid();
        AppConfig config = new() { AllowDiscovery = true, AllowViewing = true, AllowControl = false };

        state.Initialize(Identity(id), config);

        Assert.True(state.IsInitialized);
        DiscoveryRuntimeSnapshot snapshot = state.Current;
        Assert.Equal(id, snapshot.LocalDeviceId);
        Assert.Equal(config.AllowDiscovery, snapshot.AllowDiscovery);
        Assert.True(snapshot.CanAnnounce);
        Assert.True(snapshot.CanReplyToProbe);
    }

    [Fact]
    public void UpdateFromConfig_KeepsIdentityChangesFlags()
    {
        DiscoveryRuntimeState state = new();
        Guid id = Guid.NewGuid();
        state.Initialize(Identity(id), new AppConfig { AllowDiscovery = true });

        state.UpdateFromConfig(new AppConfig { AllowDiscovery = false });

        DiscoveryRuntimeSnapshot snapshot = state.Current;
        Assert.Equal(id, snapshot.LocalDeviceId);
        Assert.False(snapshot.AllowDiscovery);
        Assert.False(snapshot.CanAnnounce);
        Assert.False(snapshot.CanReplyToProbe);
    }

    [Fact]
    public void UpdateFromConfig_BeforeInitialize_IsNoOp()
    {
        DiscoveryRuntimeState state = new();

        state.UpdateFromConfig(new AppConfig { AllowDiscovery = true });

        // 身份还没到，不能因为配置先到就产生半截可广播状态。
        Assert.False(state.IsInitialized);
    }

    [Fact]
    public void AllowDiscoveryFalse_DisablesAnnounceAndProbeReply()
    {
        DiscoveryRuntimeSnapshot snapshot = new(
            Guid.NewGuid(),
            "AAAA-BBBB",
            "LOCAL",
            "0.1.0-m2",
            new string('A', 64),
            AllowDiscovery: false,
            AllowViewing: true,
            AllowControl: true);

        Assert.False(snapshot.CanAnnounce);
        Assert.False(snapshot.CanReplyToProbe);
    }

    [Fact]
    public void AllowDiscoveryTrue_EnablesAnnounceAndProbeReply()
    {
        DiscoveryRuntimeSnapshot snapshot = new(
            Guid.NewGuid(),
            "AAAA-BBBB",
            "LOCAL",
            "0.1.0-m2",
            new string('A', 64),
            AllowDiscovery: true,
            AllowViewing: true,
            AllowControl: true);

        Assert.True(snapshot.CanAnnounce);
        Assert.True(snapshot.CanReplyToProbe);
    }

    [Theory]
    [InlineData(true, true, new[] { "view", "control" })]
    [InlineData(true, false, new[] { "view" })]
    [InlineData(false, true, new[] { "control" })]
    [InlineData(false, false, new string[0])]
    public void Capabilities_FollowViewAndControlFlags(
        bool allowViewing,
        bool allowControl,
        string[] expected)
    {
        IReadOnlySet<string> capabilities =
            DiscoveryRuntimeSnapshot.BuildCapabilities(allowViewing, allowControl);

        Assert.Equal(expected.Length, capabilities.Count);
        Assert.All(expected, e => Assert.Contains(e, capabilities));
    }

    [Fact]
    public void Capabilities_NeverAdvertiseMultiMonitorInM2()
    {
        // §21：M6 之前不要提前宣布 multi-monitor。
        IReadOnlySet<string> capabilities =
            DiscoveryRuntimeSnapshot.BuildCapabilities(true, true);

        Assert.DoesNotContain("multi-monitor", capabilities);
    }

    [Fact]
    public void AppVersion_IsReadable()
    {
        // announcement 里的 appVersion 来自程序集，不应当是 unknown。
        Assert.NotEqual("0.0.0-unknown", Core.Infrastructure.AppVersion.Current);
        Assert.Contains(".", Core.Infrastructure.AppVersion.Current);
    }
}
