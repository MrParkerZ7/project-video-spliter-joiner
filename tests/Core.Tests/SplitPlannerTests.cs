using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Pure-unit tests for the split planner — validation, sort/dedupe, keyframe snapping, and
/// contiguous segment-range construction. No ffmpeg: snapping is delegated to the real
/// <see cref="MediaProbe.SnapToNearestKeyframe"/> (itself binary-free), keyframes are handed
/// in directly.
/// </summary>
public class SplitPlannerTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyList<TimeSpan> KeyframesEverySecond =
        Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList();

    // Real snapper (no binary) so we exercise the T-003 logic through the planner.
    private static readonly MediaProbe Snapper = new(new FakeFfprobeRunner("{}"));

    private static SplitPlan PlanWith(
        IReadOnlyList<TimeSpan> cuts,
        IReadOnlyList<TimeSpan>? keyframes = null,
        TimeSpan? duration = null) =>
        SplitPlanner.Plan(
            duration ?? Duration,
            cuts,
            keyframes ?? KeyframesEverySecond,
            Snapper.SnapToNearestKeyframe,
            averageGop: TimeSpan.FromSeconds(1),
            pathFor: i => $"seg{i}.mp4");

    [Fact]
    public void Plan_TwoCuts_ProducesThreeContiguousSegments()
    {
        var plan = PlanWith(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) });

        plan.Segments.Should().HaveCount(3);
        plan.InteriorSnappedCuts.Should().Equal(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6));

        plan.Segments[0].SnappedStart.Should().Be(TimeSpan.Zero);
        plan.Segments[0].SnappedEnd.Should().Be(TimeSpan.FromSeconds(3));
        plan.Segments[1].SnappedStart.Should().Be(TimeSpan.FromSeconds(3));
        plan.Segments[1].SnappedEnd.Should().Be(TimeSpan.FromSeconds(6));
        plan.Segments[2].SnappedStart.Should().Be(TimeSpan.FromSeconds(6));
        plan.Segments[2].SnappedEnd.Should().Be(Duration);
    }

    [Fact]
    public void Plan_SingleCut_ProducesTwoSegments()
    {
        var plan = PlanWith(new[] { TimeSpan.FromSeconds(5) });

        plan.Segments.Should().HaveCount(2);
        plan.Segments[0].SnappedEnd.Should().Be(TimeSpan.FromSeconds(5));
        plan.Segments[1].SnappedStart.Should().Be(TimeSpan.FromSeconds(5));
        plan.Segments[1].SnappedEnd.Should().Be(Duration);
    }

    [Fact]
    public void Plan_UnsortedCuts_AreSortedIntoAscendingSegments()
    {
        var plan = PlanWith(new[] { TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3) });

        plan.InteriorSnappedCuts.Should().BeInAscendingOrder();
        plan.InteriorSnappedCuts.Should().Equal(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Plan_NonKeyframeAlignedCut_SnapsAndReportsNegativeDelta()
    {
        // 3.4s on a 1s-GOP grid snaps to 3.0s; the second segment's start delta ≈ -0.4s.
        var plan = PlanWith(new[] { TimeSpan.FromSeconds(3.4) });

        plan.InteriorSnappedCuts.Single().Should().Be(TimeSpan.FromSeconds(3));

        // Segment[1] starts at the snapped cut; its StartDelta is the snap offset for that cut.
        plan.Segments[1].SnappedStart.Should().Be(TimeSpan.FromSeconds(3));
        plan.Segments[1].StartDelta.Should().BeCloseTo(TimeSpan.FromSeconds(-0.4), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Plan_CutAtZero_IsRejectedWithWarning()
    {
        // Zero is out of range; a lone zero cut leaves nothing valid → SplitException.
        var act = () => PlanWith(new[] { TimeSpan.Zero });
        act.Should().Throw<SplitException>();
    }

    [Fact]
    public void Plan_CutAtOrBeyondDuration_IsDroppedWithWarning()
    {
        var plan = PlanWith(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12) });

        // 10s (== duration) and 12s (> duration) dropped; only the 5s cut survives → 2 segments.
        plan.Segments.Should().HaveCount(2);
        plan.Warnings.Should().Contain(w => w.Contains("outside the file bounds"));
    }

    [Fact]
    public void Plan_AllCutsOutOfRange_Throws()
    {
        var act = () => PlanWith(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(15) });
        act.Should().Throw<SplitException>()
            .WithMessage("*No valid cut points*");
    }

    [Fact]
    public void Plan_DuplicateAndNearEqualCuts_AreDedupedWithWarning()
    {
        var plan = PlanWith(new[]
        {
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3),          // exact dup
            TimeSpan.FromSeconds(3.005),      // within 10ms epsilon → merged
            TimeSpan.FromSeconds(6),
        });

        plan.InteriorSnappedCuts.Should().Equal(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6));
        plan.Segments.Should().HaveCount(3);
        plan.Warnings.Should().Contain(w => w.Contains("merged"));
    }

    [Fact]
    public void Plan_ZeroDuration_Throws()
    {
        var act = () => PlanWith(new[] { TimeSpan.FromSeconds(1) }, duration: TimeSpan.Zero);
        act.Should().Throw<SplitException>();
    }

    [Fact]
    public void Plan_TwoCutsSnappingToSameKeyframe_DropsCollision()
    {
        // 3.4 and 3.6 both snap to 3.0 (ties/nearest on the 1s grid: 3.4→3, 3.6→4 actually).
        // Use 3.1 and 3.2 which both snap to 3.0 → the second collides and is dropped.
        var plan = PlanWith(new[] { TimeSpan.FromSeconds(3.1), TimeSpan.FromSeconds(3.2) });

        plan.InteriorSnappedCuts.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(3));
        plan.Warnings.Should().Contain(w => w.Contains("colliding") || w.Contains("merged"));
    }

    [Fact]
    public void ToSegmentTimes_FormatsInvariantCommaSeparated()
    {
        var s = SplitPlanner.ToSegmentTimes(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6.5) });
        s.Should().Be("3,6.5");
    }
}
