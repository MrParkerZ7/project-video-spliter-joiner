using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-119 (epic G-041) — the reported bug: on a ~4s keyframe grid, setting the intro at 5s and then at 6s
/// both snap to 4s (nearest-keyframe, ties resolving to the EARLIER keyframe), so a row that renders only
/// <c>Snapped</c> does not change a single pixel — a correct snap is indistinguishable from an ignored
/// click. The row must always surface the REQUESTED time plus where it landed, so the second gesture is
/// visibly acknowledged even when the snapped keyframe is unchanged.
/// </summary>
public sealed class SnapVisibilityTests
{
    /// <summary>
    /// A uniform keyframe grid + the shared <see cref="BulkFakeProbe"/>, whose SnapToNearestKeyframe
    /// mirrors the real MediaProbe exactly (nearest by distance, ties resolving to the EARLIER keyframe).
    /// </summary>
    private static List<TimeSpan> Grid(double stepSeconds, double totalSeconds)
        => Enumerable.Range(0, (int)(totalSeconds / stepSeconds) + 1)
            .Select(i => TimeSpan.FromSeconds(i * stepSeconds)).ToList();

    private static CutMarkerViewModel NewMarker(IReadOnlyList<TimeSpan> keyframes, double requestedSeconds)
        => new(new BulkFakeProbe(), () => keyframes, TimeSpan.FromSeconds(requestedSeconds));

    // ---- The bug, reproduced then fixed -------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void OnA4sGrid_5sAnd6sBothSnapTo4s_TheGrievanceIsReal()
    {
        var grid = Grid(stepSeconds: 4, totalSeconds: 60);

        NewMarker(grid, 5).Snapped.Should().Be(TimeSpan.FromSeconds(4), "|5-4|=1 beats |5-8|=3");
        NewMarker(grid, 6).Snapped.Should().Be(TimeSpan.FromSeconds(4), "6s is an exact tie and ties resolve to the EARLIER keyframe");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void MovingTheRequestFrom5sTo6s_ChangesTheVisibleRow_EvenThoughSnappedDoesNot()
    {
        var grid = Grid(stepSeconds: 4, totalSeconds: 60);
        var marker = NewMarker(grid, 5);

        var before = (Requested: marker.Requested, Snapped: marker.Snapped, Note: marker.SnapNote);

        var raised = new List<string>();
        marker.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        marker.Requested = TimeSpan.FromSeconds(6);

        // The snapped keyframe genuinely does NOT move — that is correct, lossless behaviour...
        marker.Snapped.Should().Be(before.Snapped, "both 5s and 6s snap to the same keyframe on a 4s grid");

        // ...but the row must still visibly change, or the gesture looks ignored (the bug).
        marker.Requested.Should().Be(TimeSpan.FromSeconds(6));
        marker.SnapNote.Should().NotBe(before.Note, "the delta grew from -1s to -2s, so the readout must change");
        marker.SnapNote.Should().Contain("2", "the note now reports the larger 2s offset");
        raised.Should().Contain(nameof(CutMarkerViewModel.Requested));
        raised.Should().Contain(nameof(CutMarkerViewModel.SnapNote), "the visible note re-renders even when Snapped is unchanged");
    }

    // ---- The note's content + quiet cases -----------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void SnapNote_ShowsWhereItLanded_AndTheOffset()
    {
        var grid = Grid(stepSeconds: 4, totalSeconds: 60);
        var marker = NewMarker(grid, 5);

        marker.HasSnapNote.Should().BeTrue();
        marker.SnapNote.Should().StartWith("→").And.Contain("00:04.0", "it names the keyframe the cut actually lands on");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void IdentitySnap_ShowsNoNote_SoAFineGridCarriesNoNoise()
    {
        var grid = Grid(stepSeconds: 4, totalSeconds: 60);
        var marker = NewMarker(grid, 8); // exactly on a keyframe

        marker.Delta.Should().Be(TimeSpan.Zero);
        marker.HasSnapNote.Should().BeFalse("a request already on a keyframe needs no explanation");
        marker.SnapNote.Should().BeEmpty();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void OnA2sGrid_6sLandsOn6s_WhichIsWhyTheReportRulesOutA2sGrid()
    {
        var grid = Grid(stepSeconds: 2, totalSeconds: 60);

        NewMarker(grid, 6).Snapped.Should().Be(TimeSpan.FromSeconds(6));
        NewMarker(grid, 6).HasSnapNote.Should().BeFalse();
    }

    // ---- SPEC-011#I76 — the pending (scan-in-flight) note and how it resolves ------------------

    // While the row's background keyframe scan is in flight the marker reads "→ snapping…" with
    // HasSnapNote true, and resolves to the real note when ResolveSnap clears IsSnapPending.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void WhileTheScanIsInFlight_TheNoteReadsSnapping_ThenResolves()
    {
        var grid = Grid(stepSeconds: 4, totalSeconds: 60);
        var marker = new CutMarkerViewModel(new BulkFakeProbe(), () => grid, TimeSpan.FromSeconds(5), snapPending: true);

        // In flight: the note is the "snapping…" hint and the snap is a provisional identity.
        marker.IsSnapPending.Should().BeTrue();
        marker.HasSnapNote.Should().BeTrue("a pending snap is always worth showing, even before any delta exists");
        marker.SnapNote.Should().Be("→ snapping…");
        marker.Snapped.Should().Be(TimeSpan.FromSeconds(5), "the provisional snap is the identity (Snapped == Requested)");
        marker.Delta.Should().Be(TimeSpan.Zero);

        var raised = new List<string>();
        marker.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        marker.ResolveSnap();

        marker.IsSnapPending.Should().BeFalse("ResolveSnap clears the pending flag");
        marker.Snapped.Should().Be(TimeSpan.FromSeconds(4), "5s resolves onto the 4s keyframe once the scan lands");
        marker.HasSnapNote.Should().BeTrue("the resolved delta is non-zero, so the note stays visible");
        marker.SnapNote.Should().Be(NewMarker(grid, 5).SnapNote,
            "the resolved note is exactly what the keyframes-ready path renders for the same request");
        raised.Should().Contain(nameof(CutMarkerViewModel.SnapNote), "the readout re-renders when the scan resolves");
        raised.Should().Contain(nameof(CutMarkerViewModel.HasSnapNote));
    }

    // The other half of I76: a pending snap can resolve to EMPTY — the hint disappears entirely when
    // the request turns out to already sit on a keyframe (no noise on a fine grid).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void APendingSnapThatResolvesOntoItsOwnKeyframe_FallsSilent()
    {
        var grid = Grid(stepSeconds: 4, totalSeconds: 60);
        var marker = new CutMarkerViewModel(new BulkFakeProbe(), () => grid, TimeSpan.FromSeconds(8), snapPending: true);

        marker.HasSnapNote.Should().BeTrue("while the scan is in flight the hint shows regardless of the eventual delta");
        marker.SnapNote.Should().Be("→ snapping…");

        marker.ResolveSnap();

        marker.Delta.Should().Be(TimeSpan.Zero, "8s already sits on a keyframe of the 4s grid");
        marker.HasSnapNote.Should().BeFalse("a pending snap can also resolve to NOTHING worth showing");
        marker.SnapNote.Should().BeEmpty();
    }
}
