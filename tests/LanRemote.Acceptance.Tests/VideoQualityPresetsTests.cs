// 只验证命名画质预设：预设名与合法档位的一一对应、严格解析与反查，以及按预设名为会话设档；
// 不建立真实网络、GDI 或 TLS，不代表界面已有预设选择或自动画质控制器已接线。

using System;
using System.Collections.Generic;
using LanRemote.App.Services;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Acceptance.Tests;

public sealed class VideoQualityPresetsTests
{
    [Fact]
    public void Names_are_stable_ordered_and_unique()
    {
        IReadOnlyList<string> names = VideoQualityPresets.Names;

        Assert.Equal(["low-latency", "balanced", "high-quality"], names);
        Assert.Equal(names.Count, new HashSet<string>(names, StringComparer.Ordinal).Count);
    }

    [Fact]
    public void Resolve_returns_documented_settings_for_each_name()
    {
        Assert.Equal(VideoQualitySettings.LowLatency, VideoQualityPresets.Resolve("low-latency"));
        Assert.Equal(VideoQualitySettings.Balanced, VideoQualityPresets.Resolve("balanced"));
        Assert.Equal(VideoQualitySettings.HighQuality, VideoQualityPresets.Resolve("high-quality"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("BALANCED")]
    [InlineData(" balanced")]
    [InlineData("balanced ")]
    [InlineData("high_quality")]
    public void Resolve_rejects_unknown_or_inexact_names_without_falling_back(string name)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoQualityPresets.Resolve(name));
    }

    [Fact]
    public void Resolve_rejects_null_name()
    {
        Assert.Throws<ArgumentNullException>(() => VideoQualityPresets.Resolve(null!));
    }

    [Fact]
    public void TryResolve_reports_failure_for_unknown_names_and_success_for_known_ones()
    {
        Assert.False(VideoQualityPresets.TryResolve("nope", out VideoQualitySettings? missing));
        Assert.Null(missing);

        Assert.True(VideoQualityPresets.TryResolve("high-quality", out VideoQualitySettings? found));
        Assert.Equal(VideoQualitySettings.HighQuality, found);
    }

    [Fact]
    public void NameOf_returns_the_matching_preset_name()
    {
        Assert.Equal("low-latency", VideoQualityPresets.NameOf(VideoQualitySettings.LowLatency));
        Assert.Equal("balanced", VideoQualityPresets.NameOf(VideoQualitySettings.Balanced));
        Assert.Equal("high-quality", VideoQualityPresets.NameOf(VideoQualitySettings.HighQuality));
    }

    [Theory]
    [InlineData(5, 0.75, 60, false)]
    [InlineData(20, 0.75, 61, false)]
    [InlineData(30, 0.67, 45, true)]
    public void NameOf_returns_null_for_combinations_that_are_not_a_preset(
        int fps, double scale, int quality, bool auto)
    {
        VideoQualitySettings manual = new(fps, scale, quality, auto);

        Assert.Null(VideoQualityPresets.NameOf(manual));
    }

    [Fact]
    public void Every_preset_is_within_the_strictly_allowed_ranges()
    {
        foreach (string name in VideoQualityPresets.Names)
        {
            VideoQualitySettings settings = VideoQualityPresets.Resolve(name);

            Assert.Contains(settings.TargetFps, VideoQualitySettings.AllowedFps);
            Assert.Contains(settings.Scale, VideoQualitySettings.AllowedScales);
            Assert.InRange(settings.JpegQuality, VideoQualitySettings.MinJpegQuality,
                VideoQualitySettings.MaxJpegQuality);
            Assert.False(settings.Auto);
            Assert.Equal(name, VideoQualityPresets.NameOf(settings));
        }
    }

    [Fact]
    public void SetPreset_applies_the_preset_to_one_session_without_touching_another()
    {
        SessionQualityResolver resolver = new(() => VideoQualitySettings.Balanced);
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();

        resolver.SetPreset(target, "low-latency");

        Assert.Equal(VideoQualitySettings.LowLatency, resolver.Resolve(target));
        Assert.Equal(VideoQualitySettings.Balanced, resolver.Resolve(other));
    }

    [Fact]
    public void SetPreset_rejects_unknown_name_and_keeps_the_already_held_value()
    {
        SessionQualityResolver resolver = new(() => VideoQualitySettings.Balanced);
        Guid session = Guid.NewGuid();
        resolver.Set(session, VideoQualitySettings.HighQuality);

        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.SetPreset(session, "ultra"));

        Assert.Equal(VideoQualitySettings.HighQuality, resolver.Resolve(session));
    }

    [Fact]
    public void SetPreset_rejects_empty_session_identity()
    {
        SessionQualityResolver resolver = new(() => VideoQualitySettings.Balanced);

        Assert.Throws<ArgumentException>(() => resolver.SetPreset(Guid.Empty, "balanced"));

        Assert.Equal(0, resolver.Count);
    }
}
