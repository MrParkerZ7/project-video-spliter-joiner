using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="BulkItemViewModel"/> (T-096): the two snap-handles, the computed
/// validity/row-state, the throttled keyframe scan, and the T-094 request builders. No ffmpeg, no GUI —
/// the real-snap <see cref="BulkFakeProbe"/> exercises the actual nearest-keyframe + plan-index math.
/// </summary>
public sealed class BulkItemViewModelTests
{
    private const string Path60 = @"C:\videos\ep01.mp4";

    private static SemaphoreSlim Gate() => new(3, 3);

    private static async Task<BulkItemViewModel> BuildReadyRowAsync(
        BulkFakeProbe probe, string path, double durationSeconds, double stepSeconds)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        probe.SetUniform(path, duration, stepSeconds);
        var row = new BulkItemViewModel(path, probe, Gate()) { Duration = duration };
        await row.StartKeyframeScanAsync();
        return row;
    }

    // ---- Keyframe scan + snap resolution ----------------------------------------------------

    [Fact]
    public async Task StartKeyframeScan_ResolvesHandleSnaps_AndFlipsKeyframesReady()
    {
        var probe = new BulkFakeProbe();
        probe.SetUniform(Path60, TimeSpan.FromSeconds(60), 5); // keyframes 0,5,10,…,60
        var row = new BulkItemViewModel(Path60, probe, Gate()) { Duration = TimeSpan.FromSeconds(60) };
        row.IntroEnd.Requested = TimeSpan.FromSeconds(12);

        row.KeyframesReady.Should().BeFalse("the background scan has not run yet");
        row.IntroEnd.IsSnapPending.Should().BeTrue();

        await row.StartKeyframeScanAsync();

        row.KeyframesReady.Should().BeTrue();
        row.IntroEnd.IsSnapPending.Should().BeFalse();
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10), "12s snaps to the nearest keyframe 10s");
    }

    // ---- IsValidCut / IsNoOpTrim / RowState transitions -------------------------------------

    [Fact]
    public async Task IsValidCut_And_NoOpTrim_Transitions()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2); // keyframes every 2s → MinKeptSpan 2s

        // Intro ≈ 0 with no outro → the whole file is kept → NoOpTrim, auto-disabled.
        row.RowState.Should().Be(RowState.NoOpTrim);
        row.IsEnabled.Should().BeFalse();

        // Intro ≥ (Duration − MinKeptSpan) → kept span collapses → Invalid.
        row.IntroEnd.Requested = TimeSpan.FromSeconds(58);
        row.IsValidCut.Should().BeFalse();
        row.RowState.Should().Be(RowState.Invalid);
        row.IsEnabled.Should().BeFalse();

        // A real trim → Ready + enabled, kept = outro − intro.
        row.IntroEnd.Requested = TimeSpan.FromSeconds(10);
        row.AddOutro(TimeSpan.FromSeconds(50));
        row.IsValidCut.Should().BeTrue();
        row.RowState.Should().Be(RowState.Ready);
        row.IsEnabled.Should().BeTrue();
        row.KeptDuration.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task NoOpTrim_WhenIntroZero_AndOutroSnapsToEof()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2);

        row.IntroEnd.Requested = TimeSpan.Zero;   // snaps to 0
        row.AddOutro(TimeSpan.FromSeconds(60));    // snaps to EOF

        row.IsNoOpTrim.Should().BeTrue();
        row.RowState.Should().Be(RowState.NoOpTrim);
        row.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task NoOutro_KeptDuration_IsDurationMinusIntro()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(6);

        row.HasOutro.Should().BeFalse();
        row.IsValidCut.Should().BeTrue();
        row.KeptDuration.Should().Be(TimeSpan.FromSeconds(54));
    }

    // ---- Handles ----------------------------------------------------------------------------

    [Fact]
    public async Task AddOutro_ThenClearOutro_TogglesHasOutro()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2);

        row.HasOutro.Should().BeFalse();

        row.AddOutro(TimeSpan.FromSeconds(50));
        row.HasOutro.Should().BeTrue();
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(50));

        row.ClearOutro();
        row.HasOutro.Should().BeFalse();
        row.OutroStart.Should().BeNull();
    }

    // ---- LoadFailed -------------------------------------------------------------------------

    [Fact]
    public void MarkLoadFailed_SetsLoadFailed_AndDisables()
    {
        var probe = new BulkFakeProbe();
        var row = new BulkItemViewModel(Path60, probe, Gate());

        row.MarkLoadFailed();

        row.RowState.Should().Be(RowState.LoadFailed);
        row.IsEnabled.Should().BeFalse();
    }

    // ---- T-094 request builders -------------------------------------------------------------

    [Fact]
    public async Task BuildRequest_ProducesSingleKeptSegment_TrimmedName_SelectedIndex()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(10);

        var req = row.BuildRequest();

        req.CutPoints.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(10));
        req.NamingPattern.Should().Be(KeptSegmentSelector.TrimmedNamingPattern);
        req.SelectedSegmentIndices.Should().BeEquivalentTo(new[] { 2 }, "intro survives so the kept middle is segment 2");
        req.InputPath.Should().Be(Path60);
        req.OutputDir.Should().Be(Path.GetDirectoryName(Path.GetFullPath(Path60)));
        req.Overwrite.Should().BeFalse();
    }

    [Fact]
    public async Task BuildRequest_WithOutro_HasTwoCutPoints()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(10);
        row.AddOutro(TimeSpan.FromSeconds(50));

        var req = row.BuildRequest();

        req.CutPoints.Should().Equal(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50));
        req.SelectedSegmentIndices.Should().BeEquivalentTo(new[] { 2 });
    }

    // SPEC-011#I18 — BuildRequest REFUSES to run before the file is probed: without a Duration there is
    // no plan to resolve the kept index against, so it throws rather than inventing one.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void BuildRequest_BeforeTheFileIsProbed_Throws()
    {
        var probe = new BulkFakeProbe();
        var row = new BulkItemViewModel(Path60, probe, Gate()); // constructed only — never probed, never scanned

        row.Duration.Should().BeNull("precondition: the row has no probed duration yet");

        Action act = () => _ = row.BuildRequest();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "*before the file is probed*",
                "the kept-segment index cannot be resolved without the file's duration");
    }

    [Fact]
    public async Task BuildBulkTrimItem_CarriesCutsAndTagsTheRow()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(10);
        row.AddOutro(TimeSpan.FromSeconds(50));

        var item = row.BuildBulkTrimItem();

        item.InputPath.Should().Be(Path60);
        item.IntroEnd.Should().Be(TimeSpan.FromSeconds(10));
        item.OutroStart.Should().Be(TimeSpan.FromSeconds(50));
        item.Tag.Should().BeSameAs(row);
        item.DesiredOutputPath.Should().EndWith("ep01_trimmed.mp4");
    }

    // ---- I26: dragging re-snaps against the cached keyframes, never re-scans -----------------

    [Fact]
    [Trait("serves-spec", "SPEC-014")]
    public async Task Dragging_ReSnapsAgainstCachedKeyframes_TriggersNoAdditionalKeyframeScan()
    {
        var probe = new BulkFakeProbe();
        var row = await BuildReadyRowAsync(probe, Path60, 60, 2); // keyframes 0,2,4,…,60

        // The one-time throttled ffprobe scan ran exactly once to load the keyframe list.
        row.KeyframesReady.Should().BeTrue();
        probe.GetKeyframesCallCount.Should().Be(1, "the keyframe scan runs once as the row becomes ready");

        // Simulate a drag: ~20 rapid Requested sets sweeping 9.0s → 10.9s. I26: each set re-snaps
        // Snapped against the ALREADY-LOADED keyframe list — scanning is a separate, earlier step.
        var finalRequestedSeconds = 0d;
        for (var i = 0; i < 20; i++)
        {
            finalRequestedSeconds = 9.0 + (i * 0.1); // 9.0, 9.1, …, 10.9
            row.IntroEnd.Requested = TimeSpan.FromSeconds(finalRequestedSeconds);
        }

        // PERF (no I/O on the hot path — structural, call-count based): dragging re-snaps against the
        // cached list and NEVER re-runs the scan, so the cumulative call count is STILL exactly 1.
        probe.GetKeyframesCallCount.Should().Be(
            1, "dragging re-snaps against the cached keyframes and triggers zero additional scans");

        // CORRECTNESS: the final requested ~10.9s snapped to the nearest cached keyframe (10s).
        row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(finalRequestedSeconds));
        row.IntroEnd.Snapped.Should().Be(
            TimeSpan.FromSeconds(10), "10.9s snaps to the nearest cached keyframe 10s");
    }
}
