using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-120 (epic G-041) — a coarse keyframe grid is exactly when snapping surprises the user, but the row's
/// advisory was gated on a STRICT <c>gop &gt; 4s</c>, so a file whose mean GOP is EXACTLY 4.0s — the grid
/// that produces the reported "5s and 6s both cut at 4s" — was warned about not at all. The row now also
/// reports the offset the cut ACTUALLY moved, so a locally-coarse stretch on an otherwise fine grid is
/// surfaced too.
/// </summary>
public sealed class SnapWarningTests
{
    private const string Path60 = @"C:\v\clip.mp4";

    private static SemaphoreSlim Gate() => new(3, 3);

    private static async Task<BulkItemViewModel> BuildReadyRowAsync(double stepSeconds, double introSeconds)
    {
        var probe = new BulkFakeProbe();
        var duration = TimeSpan.FromSeconds(60);
        probe.SetUniform(Path60, duration, stepSeconds);
        var row = new BulkItemViewModel(Path60, probe, Gate()) { Duration = duration };
        await row.StartKeyframeScanAsync();
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        return row;
    }

    // ---- The blind spot this closes -----------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ExactlyFourSecondGop_NowWarns_TheOldStrictThresholdSkippedIt()
    {
        // Mean GOP is exactly 4.0s — the grid behind the bug report. The old `gop > 4s` test was false.
        var row = await BuildReadyRowAsync(stepSeconds: 4, introSeconds: 5);

        row.Warning.Should().NotBeNull();
        row.Warning!.Should().Contain("coarse keyframes", "a mean GOP of exactly 4.0s is coarse and must warn");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ACutThatMovedNoticeably_IsReported_WithTheRealOffset()
    {
        // 5s on a 4s grid snaps back to 4s — a 1.0s move.
        var row = await BuildReadyRowAsync(stepSeconds: 4, introSeconds: 5);

        row.IntroEnd.Delta.Duration().Should().Be(TimeSpan.FromSeconds(1));
        row.Warning!.Should().Contain("cut moved 1.0s", "the advisory names the offset that actually happened");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ASmallSnapOnAFineGrid_DoesNotNag()
    {
        // 1s grid, request 5.1s → snaps to 5s: a 0.1s move, below the noticeable threshold.
        var row = await BuildReadyRowAsync(stepSeconds: 1, introSeconds: 5.1);

        row.IntroEnd.Delta.Duration().Should().BeLessThan(TimeSpan.FromSeconds(0.5));
        (row.Warning ?? string.Empty).Should().NotContain("cut moved", "a sub-threshold snap is not worth nagging about");
        (row.Warning ?? string.Empty).Should().NotContain("coarse keyframes", "a 1s GOP is not coarse");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnExactKeyframeRequest_ProducesNoSnapWarning()
    {
        var row = await BuildReadyRowAsync(stepSeconds: 4, introSeconds: 8); // exactly on a keyframe

        row.IntroEnd.Delta.Should().Be(TimeSpan.Zero);
        (row.Warning ?? string.Empty).Should().NotContain("cut moved");
    }

    // ---- Performance: the advisory is derived, never re-probed ---------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheWarningIsDerivedFromLoadedKeyframes_WithNoAdditionalScan()
    {
        var probe = new BulkFakeProbe();
        var duration = TimeSpan.FromSeconds(60);
        probe.SetUniform(Path60, duration, 4);
        var row = new BulkItemViewModel(Path60, probe, Gate()) { Duration = duration };
        await row.StartKeyframeScanAsync();
        row.IntroEnd.Requested = TimeSpan.FromSeconds(5);

        var scansAfterLoad = probe.GetKeyframesCallCount;

        // Reading the advisory repeatedly must not trigger any further keyframe work.
        for (var i = 0; i < 10; i++)
        {
            _ = row.Warning;
        }

        probe.GetKeyframesCallCount.Should().Be(
            scansAfterLoad,
            "the warning is computed from already-loaded keyframes — reading it never re-runs an ffprobe scan");
    }
}
