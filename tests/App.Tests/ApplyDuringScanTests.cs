using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-173 / G-057 (D-005) — a row takes a cut as soon as its duration is known, even while its keyframe scan is
/// still running, and the apply line says what will happen to it.
///
/// <para><b>The question that started it.</b> <i>"bulk cut before we could apply profile cut able, doesn't it
/// require to be snapping first?"</i> It did — every copy path skipped a row whose scan had not finished, silently,
/// with the Apply buttons enabled. None of the paths reads keyframes: they write requested times measured against
/// the row's duration, and only the run needs the snapped cut, which it already waits for.</para>
///
/// <para><b>The trap the design had to avoid.</b> Both copy loops used to classify with <c>IsValidCut</c>, which is
/// false for every row still scanning — so simply dropping the old guard would have reported every row the user
/// just fixed as <c>now invalid</c>. A scanning row is called invalid only when no snap and no precision can rescue
/// it: both ends are handles and the requested outro is at or before the intro.</para>
/// </summary>
public sealed class ApplyDuringScanTests
{
    private const string PathA = @"C:\v\a.mp4";
    private const string PathB = @"C:\v\b.mp4";
    private const string PathC = @"C:\v\c.mp4";
    private const string PathD = @"C:\v\d.mp4";

    private static SemaphoreSlim Gate() => new(3, 3);

    /// <summary>A probed row whose keyframe scan is parked at the fake probe's gate until <c>ReleaseScans</c>.</summary>
    private static (BulkItemViewModel Row, Task Scan) ScanningRow(BulkFakeProbe probe, string path, double seconds, double step)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(seconds), step);
        probe.GatedPaths.Add(path);
        var row = new BulkItemViewModel(path, probe, Gate()) { Duration = TimeSpan.FromSeconds(seconds) };
        var scan = row.StartKeyframeScanAsync();
        row.KeyframesReady.Should().BeFalse("precondition: the row is probed but its keyframe scan is held open");
        return (row, scan);
    }

    /// <summary>A probed row whose keyframe scan has already landed.</summary>
    private static async Task<BulkItemViewModel> ReadyRowAsync(BulkFakeProbe probe, string path, double seconds, double step)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(seconds), step);
        var row = new BulkItemViewModel(path, probe, Gate()) { Duration = TimeSpan.FromSeconds(seconds) };
        await row.StartKeyframeScanAsync();
        row.KeyframesReady.Should().BeTrue("precondition: the row's scan has landed");
        return row;
    }

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe) BuildVm()
    {
        var probe = new BulkFakeProbe();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), new FakeBulkTrimEngine());
        return (vm, probe);
    }

    private static async Task<BulkItemViewModel> AddReadyAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double seconds, double step, double intro, double? outro = null)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(seconds), step);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        row.IntroEnd.Requested = TimeSpan.FromSeconds(intro);
        if (outro is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    private static async Task<BulkItemViewModel> AddScanningAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double seconds, double step)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(seconds), step);
        probe.GatedPaths.Add(path);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.KeyframesReady.Should().BeFalse("precondition: the row is probed but its keyframe scan is held open");
        return row;
    }

    private static async Task<BulkItemViewModel> AddLoadFailedAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.FailProbePaths.Add(path);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.Duration.Should().BeNull("precondition: the probe failed, so the row never gets a duration");
        return row;
    }

    // ---- Repro: the copy paths reach a row whose scan is still running ----------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task ApplyProfile_ReachesAProbedRowWhoseScanIsStillRunning()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, @"C:\v\scanning.mp4", 60, 6);

        var report = CutProfileApplier.ApplyProfile(
            new CutProfile("Series", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), new[] { row });

        report.AppliedCount.Should().Be(1, "the row's duration is known, and that is all a profile needs");
        row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(10));
        row.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(50), "the outro is measured from the end: 60 − 10");
        row.IntroEnd.IsSnapPending.Should().BeTrue("the snap waits for the keyframes");
        row.IntroEnd.Display.Should().EndWith("→ snapping…");
        report.PendingSnapRows.Should().Equal(new[] { row }, "the row is reported as waiting for its scan");
        report.InvalidatedRows.Should().BeEmpty("a 40 s span is not invalid just because the snap has not landed");

        probe.ReleaseScans();
        await scan;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyProfileToSelectedAndToAll_ReachARowWhoseScanIsStillRunning()
    {
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(10), null));
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());
        var scanning = await AddScanningAsync(vm, probe, PathA, 60, 6);
        vm.SelectedProfile = vm.Profiles.Single();

        vm.SelectedItem = scanning;
        var selected = vm.ApplyProfileToSelected();

        selected!.AppliedCount.Should().Be(1, "profile → selected reaches the scanning row");
        selected.PendingSnapRows.Should().Equal(new[] { scanning });
        scanning.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(10));

        scanning.IntroEnd.Requested = TimeSpan.Zero;
        var all = vm.ApplyProfileToAll();

        all!.AppliedCount.Should().Be(1, "profile → all reaches the scanning row too");
        scanning.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(10));
        vm.ApplyReportSummary.Should().Be("Applied to 1 row(s) · 1 waiting for their scan.");

        probe.ReleaseScans();
        await scanning.CurrentScanTask;
    }

    // ---- The snap: pending until the scan lands, whatever writes in between ----------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AWriteDuringTheScan_KeepsTheSnapPending_UntilTheScanLands_ThenSnapsToTheNearestKeyframe()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);

        CutProfileApplier.ApplyProfile(new CutProfile("P", TimeSpan.FromSeconds(10), null), new[] { row });
        row.IntroEnd.Requested = TimeSpan.FromSeconds(11); // a later drag / field edit during the same scan

        row.IntroEnd.IsSnapPending.Should().BeTrue("a write during the scan never clears the pending flag");
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(11), "with no keyframes yet the provisional snap is identity");

        probe.ReleaseScans();
        await scan;

        row.IntroEnd.IsSnapPending.Should().BeFalse("the scan resolved the snap");
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12), "11 s snaps to the nearest keyframe on the 6 s grid");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task WhenTheScanFails_AnAppliedCutResolvesToItsRequestedTime()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);

        CutProfileApplier.ApplyProfile(new CutProfile("P", TimeSpan.FromSeconds(10), null), new[] { row });
        row.IntroEnd.IsSnapPending.Should().BeTrue("precondition: applied while the scan is held open");

        probe.ScanGate.TrySetException(new IOException("scan failed"));
        await scan; // the failure branch swallows the exception

        row.KeyframesReady.Should().BeTrue("a failed scan still ends the scan");
        row.IntroEnd.IsSnapPending.Should().BeFalse("the failure branch resolves the snap too");
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10), "with no keyframes the resolve is an identity snap");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task TwoAppliesDuringOneScan_TheLastWins_AndAnOutroItAdds_IsBornPending()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);

        CutProfileApplier.ApplyProfile(new CutProfile("First", TimeSpan.FromSeconds(10), null), new[] { row });
        row.OutroStart.Should().BeNull("precondition: the first profile keeps to the end of the file");
        var second = CutProfileApplier.ApplyProfile(
            new CutProfile("Second", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)), new[] { row });

        row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(20), "the last apply wins");
        row.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(50));
        row.OutroStart.IsSnapPending.Should().BeTrue("an outro added during the scan is born pending, like the intro");
        second.PendingSnapRows.Should().Equal(new[] { row });

        probe.ReleaseScans();
        await scan;

        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(18), "20 s snaps to the nearest keyframe on the 6 s grid");
        row.OutroStart.IsSnapPending.Should().BeFalse("the scan resolves the added outro too");
        row.OutroStart.Snapped.Should().Be(TimeSpan.FromSeconds(48), "50 s snaps to the nearest keyframe on the 6 s grid");
    }

    // ---- Classification: what a scanning row is reported as -------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task ASubSecondSpan_IsWaiting_NotInvalid_BecauseTheSnapMayStillRescueIt()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);

        // intro 10, outro 60 − 49.4 = 10.6: a 0.6 s span, under the 1 s floor a scanning row is measured against.
        var report = CutProfileApplier.ApplyProfile(
            new CutProfile("Tight", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(49.4)), new[] { row });

        report.PendingSnapRows.Should().Equal(new[] { row }, "only a snap can decide a positive span");
        report.InvalidatedRows.Should().BeEmpty("the D-005 1 s floor would have called this invalid — a false alarm");
        report.InvalidStillScanningCount.Should().Be(0);

        probe.ReleaseScans();
        await scan;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task ASubSecondSpan_IsStillWaiting_UnderExactCut()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);
        row.SetExactCut(true);
        row.IntroEnd.SuppressSnapNote.Should().BeTrue("precondition: the row is in Exact cut");

        var report = CutProfileApplier.ApplyProfile(
            new CutProfile("Tight", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(49.4)), new[] { row });

        report.PendingSnapRows.Should().Equal(
            new[] { row }, "one rule in both precisions: precision can be flipped after the click");
        report.InvalidatedRows.Should().BeEmpty();

        probe.ReleaseScans();
        await scan;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task AnOutroAtOrBeforeTheIntro_IsInvalidAlready_AndCountedAsStillScanning()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);

        // tail 70 on a 60 s file: the outro clamps to 0, before the intro at 10.
        var report = CutProfileApplier.ApplyProfile(
            new CutProfile("TooLong", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(70)), new[] { row });

        report.InvalidatedRows.Should().Equal(new[] { row }, "no snap can move an outro back past its intro");
        report.InvalidStillScanningCount.Should().Be(1, "it is invalid, but not red until its scan lands");
        report.PendingSnapRows.Should().BeEmpty();

        probe.ReleaseScans();
        await scan;
        row.RowState.Should().Be(RowState.Invalid, "once scanned, the row the line called invalid is red");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task TheBoundary_OutroEqualToTheIntroIsInvalid_OneTickLaterIsWaiting()
    {
        var probe = new BulkFakeProbe();
        var (equal, scanEqual) = ScanningRow(probe, PathA, 60, 6);
        var (later, scanLater) = ScanningRow(probe, PathB, 60, 6);

        var atIntro = CutProfileApplier.ApplyProfile(
            new CutProfile("Equal", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50)), new[] { equal });
        var oneTick = CutProfileApplier.ApplyProfile(
            new CutProfile("Later", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50) - TimeSpan.FromTicks(1)), new[] { later });

        equal.OutroStart!.Requested.Should().Be(equal.IntroEnd.Requested, "precondition: outro exactly at the intro");
        atIntro.InvalidatedRows.Should().Equal(new[] { equal }, "an empty span is invalid (<=, not <)");
        oneTick.PendingSnapRows.Should().Equal(new[] { later }, "one tick of span is a positive span: the scan decides");

        probe.ReleaseScans();
        await Task.WhenAll(scanEqual, scanLater);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task ATailExactlyTheDuration_PutsTheOutroAtZero_AndIsInvalidWhileScanning()
    {
        var probe = new BulkFakeProbe();
        var (row, scan) = ScanningRow(probe, PathA, 60, 6);

        var report = CutProfileApplier.ApplyProfile(
            new CutProfile("Whole", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)), new[] { row });

        row.OutroStart!.Requested.Should().Be(TimeSpan.Zero, "precondition: 60 − 60 puts the outro at the very start");
        report.InvalidatedRows.Should().Equal(new[] { row }, "an outro at 0 sits before the intro at 10");
        report.InvalidStillScanningCount.Should().Be(1);
        report.PendingSnapRows.Should().BeEmpty();

        probe.ReleaseScans();
        await scan;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task ANoOutroIntroPastTheEnd_IsWaiting_AndCanTurnReady_BecauseDurationNeverSnaps()
    {
        var probe = new BulkFakeProbe();
        probe.ByPath[PathA] = (TimeSpan.FromSeconds(60),
            Enumerable.Range(0, 21).Select(i => TimeSpan.FromSeconds(i * 2)).ToList()); // keyframes 0…40, none near 60
        probe.GatedPaths.Add(PathA);
        var row = new BulkItemViewModel(PathA, probe, Gate()) { Duration = TimeSpan.FromSeconds(60) };
        var scan = row.StartKeyframeScanAsync();

        var report = CutProfileApplier.ApplyProfile(new CutProfile("Late", TimeSpan.FromSeconds(80), null), new[] { row });

        row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(60), "precondition: the intro clamps to the duration");
        report.PendingSnapRows.Should().Equal(new[] { row }, "the upper bound, Duration, does not snap — the intro does");
        report.InvalidatedRows.Should().BeEmpty();

        probe.ReleaseScans();
        await scan;

        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(40), "the intro snaps back to the last keyframe");
        row.RowState.Should().Be(RowState.Ready, "40 < 60 − 2: a row an early 'invalid' would have written off is valid");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task AReadyRow_IsJudgedByIsValidCut_AsBefore_AndNeverReportedAsWaiting()
    {
        var probe = new BulkFakeProbe();
        var invalidAfterSnap = await ReadyRowAsync(probe, PathA, 60, 6);
        var valid = await ReadyRowAsync(probe, PathB, 60, 6);

        // intro 10 → 12, outro 20 → 18, MinKeptSpan 6: 12 < 18 − 6 is false. Valid as requested, invalid as cut.
        var invalidReport = CutProfileApplier.ApplyProfile(
            new CutProfile("Tight", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40)), new[] { invalidAfterSnap });
        var validReport = CutProfileApplier.ApplyProfile(
            new CutProfile("Wide", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12)), new[] { valid });

        invalidReport.InvalidatedRows.Should().Equal(new[] { invalidAfterSnap }, "a ready row is judged by IsValidCut");
        invalidReport.InvalidStillScanningCount.Should().Be(0, "it was ready at the click, so it is already red");
        invalidReport.PendingSnapRows.Should().BeEmpty();
        validReport.PendingSnapRows.Should().BeEmpty("a ready row is never reported as waiting");
        validReport.InvalidatedRows.Should().BeEmpty();
    }

    // ---- ⧉ apply-to-all: the unclamped path, skipped rows, the apply line --------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAll_OntoAShorterScanningRow_ReportsItInvalidStillScanning_AndCountsTheUnloadedRow()
    {
        var (vm, probe) = BuildVm();
        var source = await AddReadyAsync(vm, probe, PathA, 60, 2, intro: 10, outro: 50); // tail 10
        var shortScanning = await AddScanningAsync(vm, probe, PathB, 8, 2);
        await AddLoadFailedAsync(vm, probe, PathC);

        var report = vm.ApplyToAll(source);

        shortScanning.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(10), "⧉ does not clamp: past the 8 s end");
        shortScanning.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(-2), "8 − the 10 s tail");
        report!.InvalidatedRows.Should().Equal(new[] { shortScanning });
        report.InvalidStillScanningCount.Should().Be(1);
        report.SkippedNotLoadedCount.Should().Be(1, "the checked row whose probe failed was skipped");
        vm.ApplyReportSummary.Should().Be(
            "Applied to 1 row(s) · 1 invalid (red when their scan finishes) · 1 not loaded, skipped.");

        probe.ReleaseScans();
        await shortScanning.CurrentScanTask;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAll_CountsACheckedUnloadedRowAsSkipped_ButNeverAnUntickedOne()
    {
        var (vm, probe) = BuildVm();
        var source = await AddReadyAsync(vm, probe, PathA, 60, 2, intro: 10);
        var checkedFailed = await AddLoadFailedAsync(vm, probe, PathB);
        var untickedFailed = await AddLoadFailedAsync(vm, probe, PathC);
        untickedFailed.IsCheckedByUser = false;

        var report = vm.ApplyToAll(source);

        report!.SkippedNotLoadedCount.Should().Be(1, "only the ticked row was in scope");
        report.AppliedCount.Should().Be(0);
        checkedFailed.IntroEnd.Requested.Should().Be(TimeSpan.Zero, "nothing is written to a row with no duration");
        vm.ApplyReportSummary.Should().Be("Applied to 0 row(s) · 1 not loaded, skipped.");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task TheApplyLine_IsASnapshotOfTheClick_NotReReadWhenTheScansLand()
    {
        var (vm, probe) = BuildVm();
        var source = await AddReadyAsync(vm, probe, PathA, 60, 6, intro: 10, outro: 20); // tail 40
        var sixty = await AddScanningAsync(vm, probe, PathB, 60, 6); // outro 20: a 10 s span → waiting
        var fortyFive = await AddScanningAsync(vm, probe, PathC, 45, 6); // outro 5, before the intro → invalid

        vm.ApplyToAll(source);
        const string atTheClick = "Applied to 2 row(s) · 1 invalid (red when their scan finishes) · 1 waiting for their scan.";
        vm.ApplyReportSummary.Should().Be(atTheClick);

        probe.ReleaseScans();
        await Task.WhenAll(sixty.CurrentScanTask, fortyFive.CurrentScanTask);

        sixty.RowState.Should().Be(RowState.Invalid, "precondition: intro 12, outro 18, MinKeptSpan 6 — red once scanned");
        vm.ApplyReportSummary.Should().Be(atTheClick, "the line reports the click, not the rows' later state (D-005 OQ3)");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ARowRemovedDuringItsScan_DoesNotCrash_AndTheLineKeepsItsClickTimeCounts()
    {
        var (vm, probe) = BuildVm();
        var source = await AddReadyAsync(vm, probe, PathA, 60, 6, intro: 10, outro: 50);
        var kept = await AddScanningAsync(vm, probe, PathB, 60, 6);
        var removed = await AddScanningAsync(vm, probe, PathC, 60, 6);

        vm.ApplyToAll(source);
        const string atTheClick = "Applied to 2 row(s) · 2 waiting for their scan.";
        vm.ApplyReportSummary.Should().Be(atTheClick);

        vm.RemoveCommand.Execute(removed);
        vm.Items.Should().NotContain(removed, "precondition: the row left the list while its scan was held open");
        probe.ReleaseScans();
        await Task.WhenAll(kept.CurrentScanTask, removed.CurrentScanTask);

        vm.ApplyReportSummary.Should().Be(
            atTheClick, "the line stays a snapshot of the click until the next apply, a run or Clear (OQ3)");
        kept.RowState.Should().Be(RowState.Ready, "the surviving row snaps as usual");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExactCut_TheApplyLineReadsTheSame_WaitingForTheScan()
    {
        var (vm, probe) = BuildVm();
        var source = await AddReadyAsync(vm, probe, PathA, 60, 2, intro: 10, outro: 10.6); // tail 49.4
        var scanning = await AddScanningAsync(vm, probe, PathB, 60, 6);
        vm.ExactCut = true;
        scanning.IntroEnd.SuppressSnapNote.Should().BeTrue("precondition: Exact was switched on after the rows were added");

        vm.ApplyToAll(source);

        vm.ApplyReportSummary.Should().Be(
            "Applied to 1 row(s) · 1 waiting for their scan.", "no part of the line depends on precision");

        probe.ReleaseScans();
        await scanning.CurrentScanTask;
    }

    [Theory]
    [Trait("serves-spec", "SPEC-011")]
    [InlineData(5, 0, 0, 0, 0, "Applied to 5 row(s).")]
    [InlineData(5, 1, 0, 0, 0, "Applied to 5 row(s) · 1 now invalid (see the red rows).")]
    [InlineData(5, 0, 0, 3, 0, "Applied to 5 row(s) · 3 waiting for their scan.")]
    [InlineData(5, 1, 0, 3, 0, "Applied to 5 row(s) · 1 now invalid (see the red rows) · 3 waiting for their scan.")]
    [InlineData(20, 0, 1, 19, 0, "Applied to 20 row(s) · 1 invalid (red when their scan finishes) · 19 waiting for their scan.")]
    [InlineData(5, 1, 1, 3, 0, "Applied to 5 row(s) · 1 now invalid (see the red rows) · 1 invalid (red when their scan finishes) · 3 waiting for their scan.")]
    [InlineData(4, 0, 0, 0, 1, "Applied to 4 row(s) · 1 not loaded, skipped.")]
    [InlineData(5, 1, 1, 2, 1, "Applied to 5 row(s) · 1 now invalid (see the red rows) · 1 invalid (red when their scan finishes) · 2 waiting for their scan · 1 not loaded, skipped.")]
    [InlineData(0, 0, 0, 0, 1, "Applied to 0 row(s) · 1 not loaded, skipped.")]
    public void TheApplyLine_SaysEachPartOnlyWhenItHappened_InAFixedOrder(
        int applied, int red, int invalidStillScanning, int waiting, int skipped, string expected)
    {
        var row = new BulkItemViewModel(PathD, new BulkFakeProbe(), Gate());
        var report = new ApplyToAllReport(
            applied,
            Enumerable.Repeat(row, red + invalidStillScanning).ToList(),
            invalidStillScanning,
            Enumerable.Repeat(row, waiting).ToList(),
            skipped);

        BulkCutViewModel.FormatApplySummary(report).Should().Be(expected);
    }

    // ---- Run safety: a cut applied during a scan never reaches the run ----------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ACutAppliedDuringTheScan_HoldsTheRun_UntilTheScanLands_ThenRunsAtTheSnappedCut()
    {
        var (vm, probe) = BuildVm();
        var source = await AddReadyAsync(vm, probe, PathA, 60, 6, intro: 12, outro: 48);
        var scanning = await AddScanningAsync(vm, probe, PathB, 60, 6);

        vm.ApplyToAll(source);

        scanning.IsEnabled.Should().BeTrue("precondition: the scanning row is ticked and in the batch");
        scanning.IsValidCut.Should().BeFalse("a provisional snap is never a valid cut");
        vm.CanRunBatch.Should().BeFalse("an enabled row still scanning holds the run");

        probe.ReleaseScans();
        await scanning.CurrentScanTask;

        vm.CanRunBatch.Should().BeTrue("the scan landed and both rows are valid");
        scanning.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12), "the run uses the resolved cut");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AnUntickedScanningRow_DoesNotHoldTheRun_AndIsNotInIt()
    {
        var (vm, probe) = BuildVm();
        await AddReadyAsync(vm, probe, PathA, 60, 6, intro: 12, outro: 48);
        var scanning = await AddScanningAsync(vm, probe, PathB, 60, 6);
        scanning.IsCheckedByUser = false;

        vm.CanRunBatch.Should().BeTrue("only an ENABLED row still scanning holds the run (I37)");
        (scanning.IsEnabled && scanning.IsValidCut).Should().BeFalse("the unticked scanning row is not in the run set");

        probe.ReleaseScans();
        await scanning.CurrentScanTask;
    }

    // ---- I157: CancelScan is reserved for rows leaving the list -----------------------------------

    /// <summary>
    /// <c>CancelScan</c> clears the indexing flag WITHOUT resolving the handles. That is only safe for a row that is
    /// leaving the list: a surviving row would be left <c>KeyframesReady</c> with pending, identity-snapped handles.
    /// Asserted against source because the risk is a future caller, not today's behaviour.
    /// </summary>
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void CancelScan_IsCalledOnlyFromRemoveAndDropRows()
    {
        var src = RepoPaths.Source("src");
        var call = new Regex(@"\bCancelScan\(\)");
        var method = new Regex(@"^\s*(?:public|private|internal|protected)\b[^=;]*?\b(\w+)\s*\(");
        var callers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f =>
            {
                var lines = File.ReadAllLines(f);
                return lines
                    .Select((line, i) => (line, i))
                    .Where(x => call.IsMatch(x.line) && !x.line.Contains("void CancelScan()"))
                    .Select(x =>
                    {
                        var owner = Enumerable.Range(0, x.i + 1).Reverse()
                            .Select(j => method.Match(lines[j]))
                            .First(m => m.Success)
                            .Groups[1].Value;
                        return $"{Path.GetFileName(f)}:{owner}";
                    });
            })
            .ToList();

        callers.Should().NotBeEmpty("precondition: today's two callers are found");

        // T-171 moved the bulk row removal out of Clear into DropRows, which Clear (through DropAllRows) and the
        // automatic clear after a clean batch both use. The rule is unchanged: only a method whose job is to take rows
        // OUT of the list may cancel their scans.
        callers.Should().OnlyContain(
            c => c == "BulkCutViewModel.cs:Remove" || c == "BulkCutViewModel.cs:DropRows",
            "a scan cancelled on a surviving row must also resolve its handles and request their frames (SPEC-011 I157)");
    }
}
