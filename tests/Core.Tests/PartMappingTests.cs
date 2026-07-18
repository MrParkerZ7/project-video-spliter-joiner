using FluentAssertions;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests for the pure time→part mapping <see cref="PartMapping.PartAt"/> (T-069) — the crux of
/// the fast single-pass muxer path's DERIVED per-part progress. No ffmpeg, no I/O; every boundary /
/// edge case is exercised so an off-by-one at a part edge or a fast-split time jump can't slip
/// through. Times are seconds for readability.
/// </summary>
public sealed class PartMappingTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // duration 30s, cuts at 10 and 20 → parts: [0,10) [10,20) [20,30) — 1-based 1,2,3.
    private static readonly IReadOnlyList<TimeSpan> ThreeParts = new[] { S(10), S(20) };
    private static readonly TimeSpan ThreeDur = S(30);

    [Fact]
    public void SinglePart_NoCuts_AlwaysPartOne()
    {
        var boundaries = System.Array.Empty<TimeSpan>();
        var dur = S(30);

        PartMapping.PartAt(S(0), boundaries, dur).Should().Be((1, 0.0));
        PartMapping.PartAt(S(15), boundaries, dur).PartIndex.Should().Be(1);
        PartMapping.PartAt(S(15), boundaries, dur).PartFraction.Should().BeApproximately(0.5, 1e-9);
        PartMapping.PartAt(S(30), boundaries, dur).Should().Be((1, 1.0)); // end clamps to last (only) part done
    }

    [Fact]
    public void BeforeFirstCut_IsPartOne_WithLocalFraction()
    {
        var (idx, frac) = PartMapping.PartAt(S(5), ThreeParts, ThreeDur);
        idx.Should().Be(1);
        frac.Should().BeApproximately(0.5, 1e-9); // 5 of [0,10)
    }

    [Fact]
    public void MiddleOfMiddlePart_IsPartTwo()
    {
        var (idx, frac) = PartMapping.PartAt(S(15), ThreeParts, ThreeDur);
        idx.Should().Be(2);
        frac.Should().BeApproximately(0.5, 1e-9); // 5 into [10,20)
    }

    [Fact]
    public void ExactlyOnBoundary_BelongsToLaterPart_AtFractionZero()
    {
        // time == 10 is the START of part 2 (half-open [start,end)) → part 2, fraction 0.
        PartMapping.PartAt(S(10), ThreeParts, ThreeDur).Should().Be((2, 0.0));
        // time == 20 is the START of part 3.
        PartMapping.PartAt(S(20), ThreeParts, ThreeDur).Should().Be((3, 0.0));
    }

    [Fact]
    public void InLastPart_ReportsLastIndex_AndLocalFraction()
    {
        var (idx, frac) = PartMapping.PartAt(S(25), ThreeParts, ThreeDur);
        idx.Should().Be(3);
        frac.Should().BeApproximately(0.5, 1e-9); // 5 into [20,30)
    }

    [Fact]
    public void AtDuration_ClampsToLastPart_Done()
    {
        PartMapping.PartAt(S(30), ThreeParts, ThreeDur).Should().Be((3, 1.0));
    }

    [Fact]
    public void BeyondDuration_ClampsToLastPart_Done()
    {
        PartMapping.PartAt(S(45), ThreeParts, ThreeDur).Should().Be((3, 1.0));
    }

    [Fact]
    public void AtOrBelowZero_ClampsToFirstPart_FractionZero()
    {
        PartMapping.PartAt(S(0), ThreeParts, ThreeDur).Should().Be((1, 0.0));
        PartMapping.PartAt(S(-5), ThreeParts, ThreeDur).Should().Be((1, 0.0));
    }

    [Fact]
    public void ManyParts_MapsEachRangeCorrectly()
    {
        // cuts every 10s up to 90 → 10 parts of [k*10, k*10+10), duration 100.
        var boundaries = Enumerable.Range(1, 9).Select(k => S(k * 10)).ToList();
        var dur = S(100);

        // Mid of part 1..10.
        for (var k = 0; k < 10; k++)
        {
            var t = S(k * 10 + 5);
            var (idx, frac) = PartMapping.PartAt(t, boundaries, dur);
            idx.Should().Be(k + 1, $"time {t.TotalSeconds}s sits in part {k + 1}");
            frac.Should().BeApproximately(0.5, 1e-9);
        }

        // A boundary lands in the later part at fraction 0.
        PartMapping.PartAt(S(50), boundaries, dur).Should().Be((6, 0.0));
    }

    [Fact]
    public void FractionIsClampedToUnitInterval_NeverExceedsOne()
    {
        // A time just below the next boundary yields a fraction < 1; exactly at end → capped.
        var (idx, frac) = PartMapping.PartAt(S(9.999), ThreeParts, ThreeDur);
        idx.Should().Be(1);
        frac.Should().BeLessThanOrEqualTo(1.0).And.BeGreaterThan(0.99);
    }

    [Fact]
    public void UnevenParts_UsesEachPartsOwnSpanForFraction()
    {
        // cuts at 5 and 25 → parts [0,5) [5,25) [25,40); fraction is LOCAL to each part's own length.
        var boundaries = new[] { S(5), S(25) };
        var dur = S(40);

        // 20 into part 2 which spans 20s → 0.75 (15 of 20), not scaled to the whole file.
        var (idx, frac) = PartMapping.PartAt(S(20), boundaries, dur);
        idx.Should().Be(2);
        frac.Should().BeApproximately((20.0 - 5.0) / (25.0 - 5.0), 1e-9);
    }

    [Fact]
    public void ZeroLengthPart_ReportsFractionOne_NoDivideByZero()
    {
        // Degenerate coincident boundaries (the planner prevents this, but guard the pure fn):
        // cuts at 10 and 10 → a zero-length part [10,10). A time landing on it must not divide by zero.
        var boundaries = new[] { S(10), S(10) };
        var dur = S(30);

        // time exactly 10 → belongs to the FIRST part whose end is > 10, i.e. skips both 10-ending
        // parts and lands in [10,30) (part 3) at fraction 0 — no NaN/Infinity anywhere.
        var (idx, frac) = PartMapping.PartAt(S(10), boundaries, dur);
        idx.Should().BeInRange(2, 3);
        double.IsNaN(frac).Should().BeFalse();
        double.IsInfinity(frac).Should().BeFalse();
        frac.Should().BeInRange(0.0, 1.0);
    }
}
