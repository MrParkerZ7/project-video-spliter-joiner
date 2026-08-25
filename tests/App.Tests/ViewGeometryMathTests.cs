using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-105 coverage for the pure geometry/math extracted from the WPF views so it is testable without
/// a visual (SPEC-014 I31/I32/I33/I35): the timeline marker-tick hit test + waveform per-column peak
/// (<see cref="TimelineMath"/>), and the Bulk scrub seconds→pixel mapping, keep-span, and handle
/// pick (<see cref="BulkScrubMath"/>). The views now delegate to these — behaviour is unchanged.
/// </summary>
public sealed class ViewGeometryMathTests
{
    // ---- SPEC-014 I31 — TimelineMath.NearestNormalizedIndex (marker-tick hit test) -----------

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void NearestNormalizedIndex_ReturnsNearestWithinRadius()
    {
        // positions on a 100px strip: [0, 50, 100]; click at 52 → nearest is index 1 (dist 2 ≤ 6).
        var i = TimelineMath.NearestNormalizedIndex(new[] { 0.0, 0.5, 1.0 }, xPx: 52, width: 100, radiusPx: 6);
        i.Should().Be(1);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void NearestNormalizedIndex_NoneWithinRadius_ReturnsMinusOne()
    {
        // click at 25 → dists [25, 25, 75]; nearest 25 > 6 → no hit.
        var i = TimelineMath.NearestNormalizedIndex(new[] { 0.0, 0.5, 1.0 }, xPx: 25, width: 100, radiusPx: 6);
        i.Should().Be(-1);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void NearestNormalizedIndex_EquidistantTie_LaterEntryWins()
    {
        // positions [48, 52]; click at 50 → both dist 2 (≤ 6) → the later index wins (matches the view's <=).
        var i = TimelineMath.NearestNormalizedIndex(new[] { 0.48, 0.52 }, xPx: 50, width: 100, radiusPx: 6);
        i.Should().Be(1);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void NearestNormalizedIndex_EmptyList_ReturnsMinusOne()
    {
        TimelineMath.NearestNormalizedIndex(System.Array.Empty<double>(), 10, 100, 6).Should().Be(-1);
    }

    // ---- SPEC-014 I35 — TimelineMath.PeakForColumn (waveform max-per-column bucketing) --------

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PeakForColumn_MorePeaksThanColumns_KeepsMaxPerColumn()
    {
        var peaks = new[] { 0.1f, 0.9f, 0.2f, 0.3f };
        TimelineMath.PeakForColumn(peaks, column: 0, columns: 2).Should().Be(0.9f); // max(0.1, 0.9)
        TimelineMath.PeakForColumn(peaks, column: 1, columns: 2).Should().Be(0.3f); // max(0.2, 0.3)
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PeakForColumn_FewerPeaksThanColumns_SamplesNearestSourcePeak()
    {
        var peaks = new[] { 0.5f, 0.7f };
        // 2 peaks spread over 4 columns → [0.5, 0.5, 0.7, 0.7].
        TimelineMath.PeakForColumn(peaks, 0, 4).Should().Be(0.5f);
        TimelineMath.PeakForColumn(peaks, 1, 4).Should().Be(0.5f);
        TimelineMath.PeakForColumn(peaks, 2, 4).Should().Be(0.7f);
        TimelineMath.PeakForColumn(peaks, 3, 4).Should().Be(0.7f);
    }

    // ---- SPEC-014 I32 — BulkScrubMath.SecondsToX + KeepSpan -----------------------------------

    [Trait("serves-spec", "SPEC-014")]
    [Theory]
    [InlineData(30, 60, 200, 100)] // half → 100px
    [InlineData(0, 60, 200, 0)]    // start → 0
    [InlineData(60, 60, 200, 200)] // end → full width
    [InlineData(90, 60, 200, 200)] // past end → clamped to width
    [InlineData(-5, 60, 200, 0)]   // before start → clamped to 0
    public void SecondsToX_MapsAndClamps(double seconds, double total, double width, double expected)
    {
        BulkScrubMath.SecondsToX(seconds, total, width).Should().Be(expected);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void KeepSpan_OrdersTheTwoHandleXs_EvenWhenCrossed()
    {
        BulkScrubMath.KeepSpan(30, 90).Should().Be((30d, 90d));
        BulkScrubMath.KeepSpan(90, 30).Should().Be((30d, 90d)); // crossed during a drag → still (min, max)
    }

    // ---- SPEC-014 I33 — BulkScrubMath.PickHandle (nearer handle within 8px, tie by Y) ---------

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PickHandle_NearIntro_WithinRadius_PicksIntro()
    {
        BulkScrubMath.PickHandle(introX: 50, outroX: 150, posX: 53, posY: 10, height: 20, radiusPx: 8)
            .Should().Be(ScrubHandle.Intro);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PickHandle_NearOutro_WithinRadius_PicksOutro()
    {
        BulkScrubMath.PickHandle(introX: 50, outroX: 150, posX: 147, posY: 10, height: 20, radiusPx: 8)
            .Should().Be(ScrubHandle.Outro);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PickHandle_OutsideRadius_PicksNone()
    {
        BulkScrubMath.PickHandle(introX: 50, outroX: 150, posX: 100, posY: 10, height: 20, radiusPx: 8)
            .Should().Be(ScrubHandle.None);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PickHandle_NoOutro_OnlyIntroIsHitTestable()
    {
        BulkScrubMath.PickHandle(introX: 50, outroX: null, posX: 53, posY: 10, height: 20, radiusPx: 8)
            .Should().Be(ScrubHandle.Intro);
        BulkScrubMath.PickHandle(introX: 50, outroX: null, posX: 100, posY: 10, height: 20, radiusPx: 8)
            .Should().Be(ScrubHandle.None);
    }

    [Trait("serves-spec", "SPEC-014")]
    [Fact]
    public void PickHandle_EquidistantTie_BrokenByVertical_TopIntro_BottomOutro()
    {
        // introX 50, outroX 54, cursor at x=52 → both dist 2 (tie < 0.5px apart); height 20 → mid at 10.
        BulkScrubMath.PickHandle(50, 54, posX: 52, posY: 5, height: 20, radiusPx: 8).Should().Be(ScrubHandle.Intro);
        BulkScrubMath.PickHandle(50, 54, posX: 52, posY: 15, height: 20, radiusPx: 8).Should().Be(ScrubHandle.Outro);
    }
}
