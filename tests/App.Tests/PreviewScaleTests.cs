using FluentAssertions;
using VideoSplitJoiner.App.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the pure <see cref="PreviewScale"/> geometry helper (T-024). No WPF, no FFME —
/// just size math: cap a 4K source to a ~1080p preview, preserve aspect, never upscale, keep
/// dimensions even, and stay safe on garbage input.
/// </summary>
public sealed class PreviewScaleTests
{
    [Fact]
    public void FourK_capped_to_1080_gives_1920x1080()
    {
        var (w, h) = PreviewScale.ComputeTarget(3840, 2160, 1080);
        w.Should().Be(1920);
        h.Should().Be(1080);
    }

    [Fact]
    public void FourK_capped_to_720_gives_1280x720()
    {
        var (w, h) = PreviewScale.ComputeTarget(3840, 2160, 720);
        w.Should().Be(1280);
        h.Should().Be(720);
    }

    [Fact]
    public void Source_already_smaller_than_cap_is_returned_unchanged_never_upscaled()
    {
        var (w, h) = PreviewScale.ComputeTarget(1280, 720, 1080);
        w.Should().Be(1280);
        h.Should().Be(720);
    }

    [Fact]
    public void Source_exactly_at_cap_is_unchanged()
    {
        var (w, h) = PreviewScale.ComputeTarget(1920, 1080, 1080);
        w.Should().Be(1920);
        h.Should().Be(1080);
    }

    [Theory]
    [InlineData(3840, 2160, 1080, 1920, 1080)] // 16:9 4K
    [InlineData(4096, 2160, 1080, 2048, 1080)] // DCI 4K (17:9)
    [InlineData(2560, 1440, 1080, 1920, 1080)] // 1440p → 1080p
    public void Aspect_ratio_is_preserved_on_downscale(int sw, int sh, int cap, int ew, int eh)
    {
        var (w, h) = PreviewScale.ComputeTarget(sw, sh, cap);
        w.Should().Be(ew);
        h.Should().Be(eh);
    }

    [Fact]
    public void Odd_source_dimensions_round_to_even()
    {
        // 1921x1081 is already under a 1080 cap on height? height 1081 > 1080 → downscale slightly.
        var (w, h) = PreviewScale.ComputeTarget(1921, 1081, 1080);
        (w % 2).Should().Be(0);
        (h % 2).Should().Be(0);
    }

    [Fact]
    public void Odd_source_under_cap_is_floored_to_even_not_upscaled()
    {
        var (w, h) = PreviewScale.ComputeTarget(1281, 721, 1080);
        w.Should().Be(1280);
        h.Should().Be(720);
    }

    [Theory]
    [InlineData(0, 0, 1080)]
    [InlineData(-1, 100, 1080)]
    [InlineData(100, -1, 1080)]
    [InlineData(3840, 2160, 0)]
    [InlineData(3840, 2160, -5)]
    public void Non_positive_inputs_are_safe_and_return_source_unchanged(int sw, int sh, int cap)
    {
        var (w, h) = PreviewScale.ComputeTarget(sw, sh, cap);
        w.Should().Be(sw);
        h.Should().Be(sh);
    }

    [Fact]
    public void ShouldDownscale_true_for_4k_false_for_720p()
    {
        PreviewScale.ShouldDownscale(3840, 2160, 1080).Should().BeTrue();
        PreviewScale.ShouldDownscale(1280, 720, 1080).Should().BeFalse();
        PreviewScale.ShouldDownscale(1920, 1080, 1080).Should().BeFalse();
    }

    [Fact]
    public void ShouldDownscale_false_for_unknown_dimensions()
    {
        PreviewScale.ShouldDownscale(0, 0, 1080).Should().BeFalse();
    }

    [Fact]
    public void BuildScaleFilter_emits_even_scale_for_4k()
    {
        PreviewScale.BuildScaleFilter(3840, 2160, 1080).Should().Be("scale=1920:1080");
        PreviewScale.BuildScaleFilter(3840, 2160, 720).Should().Be("scale=1280:720");
    }

    [Fact]
    public void BuildScaleFilter_null_when_no_downscale_needed()
    {
        PreviewScale.BuildScaleFilter(1280, 720, 1080).Should().BeNull();
        PreviewScale.BuildScaleFilter(1920, 1080, 1080).Should().BeNull();
        PreviewScale.BuildScaleFilter(0, 0, 1080).Should().BeNull();
    }
}
