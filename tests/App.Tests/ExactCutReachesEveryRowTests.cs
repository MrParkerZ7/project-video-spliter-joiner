using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-176 / G-057 — Exact cut reaches every row: a row added after the toggle, and an outro handle added later,
/// follow the tab's precision.
///
/// <para><b>What was wrong.</b> The <c>ExactCut</c> setter pushed the precision onto the rows in the list at that
/// moment and nowhere else. A row added afterwards was judged on its keyframe-SNAPPED time and kept its snap note,
/// while Run cut it exactly, at the REQUESTED time — so a coarse-GOP row whose request snaps back to 0 was excluded
/// as "nothing to trim yet", a trim Exact would have performed. An outro handle added after the toggle likewise
/// advertised a snap Exact never makes. Same class as T-127 review finding #1: eligibility must be measured against
/// the cut the batch actually performs.</para>
///
/// <para><b>Vacuity guard.</b> Every note assertion first shows the note WOULD appear without suppression — the
/// handle is pending, or <c>Snapped != Requested</c> — so a setup that lands on a keyframe fails loudly.</para>
/// </summary>
public sealed class ExactCutReachesEveryRowTests
{
    private const string PathA = @"C:\v\a.mp4";
    private const string PathB = @"C:\v\b.mp4";

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe) Build()
    {
        var probe = new BulkFakeProbe();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), new FakeBulkTrimEngine());
        return (vm, probe);
    }

    /// <summary>A row on the 4 s grid of <see cref="ExactCutModeTests"/>, its scan landed.</summary>
    private static async Task<BulkItemViewModel> AddReadyAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double seconds = 60, double step = 4)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(seconds), step);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        row.KeyframesReady.Should().BeTrue("precondition: the row's scan has landed");
        return row;
    }

    /// <summary>A probed row whose keyframe scan is parked at the fake probe's gate until <c>ReleaseScans</c>.</summary>
    private static async Task<BulkItemViewModel> AddScanningAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 4);
        probe.GatedPaths.Add(path);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.KeyframesReady.Should().BeFalse("precondition: the row is probed but its keyframe scan is held open");
        return row;
    }

    // ---- A row added after the toggle ---------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_ARowAddedAfterTheToggle_WhoseRequestSnapsBackToZero_IsEligible()
    {
        var (vm, probe) = Build();
        vm.ExactCut = true;

        // An 8 s GOP: 3 s is nearest to keyframe 0, so Snapped == 0 while Requested stays 3 s.
        var row = await AddReadyAsync(vm, probe, PathA, seconds: 120, step: 8);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(3);
        row.IntroEnd.Snapped.Should().Be(TimeSpan.Zero, "precondition: 3 s snaps back to the 0 s keyframe");

        row.IsNoOpTrim.Should().BeFalse("under Exact the cut lands at the requested 3 s, which is a real trim");
        row.IsValidCut.Should().BeTrue();
        row.IsEnabled.Should().BeTrue("a row Exact can cut must not drop out of the batch because it was added late");
        row.ExclusionReason.Should().BeNull();
        row.IntroEnd.HasSnapNote.Should().BeFalse("Exact makes no snap, so the row must not advertise one");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_ARowAddedWhileItsScanIsHeld_HidesItsNoteBeforeTheScanLands()
    {
        var (vm, probe) = Build();
        vm.ExactCut = true;

        var row = await AddScanningAsync(vm, probe, PathA);

        row.IntroEnd.IsSnapPending.Should().BeTrue("precondition: without suppression this row would read → snapping…");
        row.IntroEnd.HasSnapNote.Should().BeFalse("under Exact a scanning row does not claim it is snapping");

        probe.ReleaseScans();
        await row.CurrentScanTask;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_OnTheDropPath_EveryRowHidesItsNote_FromTheMomentItAppears()
    {
        var (vm, probe) = Build();
        vm.ExactCut = true;
        foreach (var path in new[] { PathA, PathB })
        {
            probe.SetUniform(path, TimeSpan.FromSeconds(60), 4);
            probe.GatedPaths.Add(path);
        }

        // The fake probe completes synchronously, so only an assertion taken AT the Add event can tell a row that
        // had its precision from birth from one that got it later in the same AddFilesAsync call.
        var seen = new List<(string Path, bool Pending, bool HasNote)>();
        vm.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (BulkItemViewModel row in e.NewItems!)
                {
                    seen.Add((row.Path, row.IntroEnd.IsSnapPending, row.IntroEnd.HasSnapNote));
                }
            }
        };

        await vm.AddDroppedFilesAsync(new[] { PathA, PathB });
        probe.ReleaseScans();
        await Task.WhenAll(vm.Items.Select(i => i.CurrentScanTask));

        seen.Select(s => s.Path).Should().Equal(new[] { PathA, PathB }, "one Add event per dropped video");
        seen.Should().OnlyContain(s => s.Pending, "precondition: each row is born snap-pending");
        seen.Should().OnlyContain(s => !s.HasNote, "precision holds from the row's first moment, not after its probe");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_ARowWhoseProbeFails_KeepsNoSnapNote_AndCostsNoScan()
    {
        var (vm, probe) = Build();
        vm.ExactCut = true;
        probe.FailProbePaths.Add(PathA);

        await vm.AddFilesAsync(new[] { PathA });
        var row = vm.Items.Single();

        row.RowState.Should().Be(RowState.LoadFailed);
        row.IntroEnd.IsSnapPending.Should().BeTrue("precondition: a failed probe never resolves the snap");
        row.IntroEnd.HasSnapNote.Should().BeFalse("under Exact the permanent → snapping… is hidden too");
        probe.GetKeyframesCallCount.Should().Be(0, "setting the precision on a fresh row is pure VM state");
    }

    // ---- An outro handle added after the toggle ------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_AnOutroAddedLater_OnAReadyRow_ShowsNoSnapNote()
    {
        var (vm, probe) = Build();
        var row = await AddReadyAsync(vm, probe, PathA);
        vm.ExactCut = true;

        row.AddOutro(TimeSpan.FromSeconds(45));

        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(44), "precondition: 45 s snaps back to 44 s");
        row.OutroStart.HasSnapNote.Should().BeFalse("Exact cuts at 45 s, so a −1.0 s note would be false");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_AProfileTailAddedLater_OnARowWithNoOutro_ShowsNoSnapNote()
    {
        var (vm, probe) = Build();
        var row = await AddReadyAsync(vm, probe, PathA);
        row.OutroStart.Should().BeNull("precondition: the profile has to ADD the outro handle");
        vm.ExactCut = true;

        CutProfileApplier.ApplyProfile(
            new CutProfile("Series", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)), new[] { row });

        row.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(45));
        row.OutroStart.Snapped.Should().Be(TimeSpan.FromSeconds(44), "precondition: 45 s snaps back to 44 s");
        row.OutroStart.HasSnapNote.Should().BeFalse();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_AnOutroAddedLater_OnAScanningRow_StaysHidden_WhenTheScanLands()
    {
        var (vm, probe) = Build();
        var row = await AddScanningAsync(vm, probe, PathA);
        vm.ExactCut = true;

        row.AddOutro(TimeSpan.FromSeconds(45));

        row.OutroStart!.IsSnapPending.Should().BeTrue("precondition: the new handle is born pending on a scanning row");
        row.OutroStart.HasSnapNote.Should().BeFalse("under Exact it does not read → snapping…");

        probe.ReleaseScans();
        await row.CurrentScanTask;

        row.OutroStart.Snapped.Should().Be(TimeSpan.FromSeconds(44), "precondition: the landed snap moves the outro");
        row.OutroStart.HasSnapNote.Should().BeFalse("and it stays hidden once the scan lands");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_ARowAddedLater_ThenGivenAnOutro_ShowsNoNoteOnEitherHandle()
    {
        var (vm, probe) = Build();
        vm.ExactCut = true;
        var row = await AddReadyAsync(vm, probe, PathA);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(5);

        row.AddOutro(TimeSpan.FromSeconds(45));

        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(4), "precondition: 5 s snaps to 4 s");
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(44), "precondition: 45 s snaps back to 44 s");
        row.IntroEnd.HasSnapNote.Should().BeFalse("the row took the precision when it was added");
        row.OutroStart.HasSnapNote.Should().BeFalse("and the outro took it from the row when it was added");
    }

    // ---- Guards: Lossless is untouched, and a flip back still restores every note -------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task LosslessThroughout_AnAddedRowAndAnAddedOutro_StillShowTheirNotes()
    {
        var (vm, probe) = Build();
        var row = await AddReadyAsync(vm, probe, PathA);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(5);

        row.AddOutro(TimeSpan.FromSeconds(45));

        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(4), "precondition: 5 s snaps to 4 s");
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(44), "precondition: 45 s snaps back to 44 s");
        row.IntroEnd.HasSnapNote.Should().BeTrue("Lossless really does move the intro, and the row must say so");
        row.OutroStart.HasSnapNote.Should().BeTrue("and the outro");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExact_AnAddedRowAndOutro_GetTheirNotesBack_WhenTheTabFlipsToLossless()
    {
        var (vm, probe) = Build();
        vm.ExactCut = true;
        var row = await AddReadyAsync(vm, probe, PathA);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(5);
        row.AddOutro(TimeSpan.FromSeconds(45));

        vm.ExactCut = false;

        row.IntroEnd.HasSnapNote.Should().BeTrue("the I91 flip reaches rows added under Exact");
        row.OutroStart!.HasSnapNote.Should().BeTrue("and outros added under Exact");
    }
}
