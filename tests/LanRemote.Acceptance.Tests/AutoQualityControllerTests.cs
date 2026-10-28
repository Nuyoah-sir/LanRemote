using System;
using LanRemote.App.Services;
using Xunit;

namespace LanRemote.Acceptance.Tests;

/// <summary>自动画质决策核心：只按连续真实样本在命名预设之间逐级升降；无样本/手工档位绝不改档。</summary>
public sealed class AutoQualityControllerTests
{
    private static readonly Guid Session = new("b6a0f4c2-7c47-4a93-9b0f-2a3f6a3d9c11");
    private static readonly Guid Other = new("0f2d1f39-5a0e-4b5e-98ff-2a0b6d1f4b77");

    [Fact]
    public void Named_presets_are_ordered_low_latency_balanced_high_quality()
    {
        Assert.Equal(new[] { "low-latency", "balanced", "high-quality" }, VideoQualityPresets.Names);
        Assert.All(VideoQualityPresets.Names, name => Assert.False(VideoQualityPresets.Resolve(name).Auto));
    }

    [Fact]
    public void Unknown_or_manual_preset_is_never_managed_and_clears_counters()
    {
        AutoQualityController controller = new();
        Assert.Null(controller.Evaluate(Session, Bad("high-quality")).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Bad("high-quality")).TargetPreset);
        AutoQualityController.Decision reset = controller.Evaluate(Session, Bad("manual-or-unknown"));
        Assert.Null(reset.TargetPreset);
        Assert.Equal(0, reset.BadSamples);
        Assert.Null(controller.Evaluate(Session, Bad("high-quality")).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Bad("high-quality")).TargetPreset);
        Assert.Equal("balanced", controller.Evaluate(Session, Bad("high-quality")).TargetPreset);
    }

    [Fact]
    public void Missing_round_trip_or_unstarted_fps_is_not_a_downgrade_signal()
    {
        AutoQualityController controller = new();
        controller.Evaluate(Session, Bad("high-quality"));
        controller.Evaluate(Session, Bad("high-quality"));
        AutoQualityController.Decision ignored = controller.Evaluate(Session,
            new AutoQualityController.Observation(null, 30, "high-quality"));
        Assert.Null(ignored.TargetPreset);
        Assert.Equal(2, ignored.BadSamples);
        AutoQualityController.Decision zero = controller.Evaluate(Session,
            new AutoQualityController.Observation(TimeSpan.FromMilliseconds(300), 0, "high-quality"));
        Assert.Null(zero.TargetPreset);
        Assert.Equal(2, zero.BadSamples);
        // 已有计数不清零：再来一个真实坏样本才换档，不用“无样本”凑数。
        Assert.Equal("balanced", controller.Evaluate(Session, Bad("high-quality")).TargetPreset);
    }

    [Theory]
    [InlineData("high-quality", "balanced", "low-latency")]
    [InlineData("balanced", "low-latency", null)]
    public void Consecutive_bad_samples_downgrade_one_step_until_the_floor(string start, string first, string? second)
    {
        AutoQualityController controller = new();
        string current = start;
        Assert.Null(controller.Evaluate(Session, Bad(current)).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Bad(current)).TargetPreset);
        AutoQualityController.Decision down = controller.Evaluate(Session, Bad(current));
        Assert.Equal(first, down.TargetPreset);
        Assert.Equal(0, down.BadSamples);

        current = first;
        Assert.Null(controller.Evaluate(Session, Bad(current)).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Bad(current)).TargetPreset);
        Assert.Equal(second, controller.Evaluate(Session, Bad(current)).TargetPreset);
    }

    [Theory]
    [InlineData("low-latency", "balanced", "high-quality")]
    [InlineData("balanced", "high-quality", null)]
    public void Consecutive_good_samples_upgrade_one_step_until_the_ceiling(string start, string first, string? second)
    {
        AutoQualityController controller = new();
        string current = start;
        Assert.Null(controller.Evaluate(Session, Good(current)).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Good(current)).TargetPreset);
        AutoQualityController.Decision up = controller.Evaluate(Session, Good(current));
        Assert.Equal(first, up.TargetPreset);
        Assert.Equal(0, up.GoodSamples);

        current = first;
        Assert.Null(controller.Evaluate(Session, Good(current)).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Good(current)).TargetPreset);
        Assert.Equal(second, controller.Evaluate(Session, Good(current)).TargetPreset);
    }

    [Fact]
    public void Low_fps_alone_downgrades_and_mixed_hysteresis_resets_counters()
    {
        AutoQualityController controller = new();
        AutoQualityController.Observation lowFps = new(TimeSpan.FromMilliseconds(10), 12, "high-quality");
        Assert.Null(controller.Evaluate(Session, lowFps).TargetPreset);
        Assert.Null(controller.Evaluate(Session, lowFps).TargetPreset);
        Assert.Equal("balanced", controller.Evaluate(Session, lowFps).TargetPreset);

        // 滞回区间（RTT 80ms、帧率达标）既不算好也不算坏，清零计数。
        AutoQualityController.Observation mixed = new(TimeSpan.FromMilliseconds(80), 20, "balanced");
        AutoQualityController.Decision reset = controller.Evaluate(Session, mixed);
        Assert.Equal(0, reset.GoodSamples);
        Assert.Equal(0, reset.BadSamples);
        Assert.Null(controller.Evaluate(Session, Bad("balanced")).TargetPreset);
        Assert.Null(controller.Evaluate(Session, Bad("balanced")).TargetPreset);
        Assert.Equal("low-latency", controller.Evaluate(Session, Bad("balanced")).TargetPreset);
    }

    [Fact]
    public void Counters_are_per_session_and_reset_or_clear_drops_them()
    {
        AutoQualityController controller = new();
        controller.Evaluate(Session, Bad("high-quality"));
        controller.Evaluate(Session, Bad("high-quality"));
        controller.Evaluate(Other, Good("low-latency"));
        controller.Evaluate(Other, Good("low-latency"));
        Assert.Equal(2, controller.Snapshot(Session).BadSamples);
        Assert.Equal(2, controller.Snapshot(Other).GoodSamples);
        Assert.Equal("balanced", controller.Evaluate(Other, Good("low-latency")).TargetPreset);
        Assert.Equal(2, controller.Snapshot(Session).BadSamples);

        controller.Reset(Session);
        Assert.Equal(0, controller.Snapshot(Session).BadSamples);
        controller.Evaluate(Session, Bad("high-quality"));
        controller.Evaluate(Other, Bad("balanced"));
        controller.Clear();
        Assert.Equal(0, controller.Snapshot(Session).BadSamples);
        Assert.Equal(0, controller.Snapshot(Other).BadSamples);
    }

    [Fact]
    public void Empty_session_identifier_is_rejected()
    {
        AutoQualityController controller = new();
        Assert.Throws<ArgumentException>(() => controller.Evaluate(Guid.Empty, Good("balanced")));
    }

    private static AutoQualityController.Observation Bad(string preset) =>
        new(TimeSpan.FromMilliseconds(200), 30, preset);

    private static AutoQualityController.Observation Good(string preset) =>
        new(TimeSpan.FromMilliseconds(10), preset == "low-latency" ? 30 : 20, preset);
}
