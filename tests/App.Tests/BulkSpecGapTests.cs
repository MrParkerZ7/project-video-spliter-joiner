using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// SPEC-011 bulk-cut-screen gaps (todo-automate): LastInputDir memory (I5), the per-row scan supersede
/// (I14) + scan-throws identity fallback (I15) + Warning notes (I16), and the BulkCutViewModel apply-to-all
/// guards / outro-clear / CanExecute (I20/I22/I24) plus the late-post terminal guard (I43). Reuses the
/// existing internal Bulk fakes (<see cref="BulkFakeProbe"/>, <see cref="FakeBulkTrimEngine"/>,
/// <see cref="ThrowingFakeSplitEngine"/>, <see cref="FakeThumbnailService"/>, <see cref="FakeSettings"/>).
/// </summary>
public sealed class BulkSpecGapTests
{
    private static SemaphoreSlim Gate() => new(3, 3);

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeSettings Settings) BuildVm()
    {
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        var vm = new BulkCutViewModel(probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());
        return (vm, probe, settings);
    }

    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double durationSeconds, double stepSeconds,
        double introSeconds, double? outroSeconds = null)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        if (outroSeconds is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    private static async Task<BulkItemViewModel> ReadyRowAsync(
        BulkFakeProbe probe, string path, double durationSeconds, double stepSeconds,
        double introSeconds, double? outroSeconds = null)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        var row = new BulkItemViewModel(path, probe, Gate()) { Duration = TimeSpan.FromSeconds(durationSeconds) };
        await row.StartKeyframeScanAsync();
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        if (outroSeconds is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    /// <summary>A probe whose <see cref="GetKeyframesAsync"/> THROWS (drives the scan-error fallback, I15).</summary>
    private sealed class ThrowingKeyframeProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => throw new InvalidOperationException("scan boom");

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero); // never reached: empty keyframes → identity in the marker VM

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    // ---- I5: LastInputDir memory ------------------------------------------------------------

    // SPEC-011#I5 — AddFilesAsync records the last-added file's directory into IAppSettings.LastInputDir.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AddFiles_RecordsLastInputDir()
    {
        var (vm, probe, settings) = BuildVm();
        probe.SetUniform(@"C:\v\a.mp4", TimeSpan.FromSeconds(60), 2);

        await vm.AddFilesAsync(new[] { @"C:\v\a.mp4" });

        settings.LastInputDir.Should().Be(@"C:\v", "the last-added file's directory seeds the next file-picker");
    }

    // ---- I14: per-row scan supersede --------------------------------------------------------

    // SPEC-011#I14 — StartKeyframeScanAsync supersedes an in-flight scan (Interlocked.Exchange of the CTS
    // cancels the prior one); only the current CTS commits, so the row ends with the SECOND scan's result.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task StartKeyframeScan_SupersedesInFlight_SecondScanWins()
    {
        var probe = new BulkFakeProbe();
        const string path = @"C:\v\ep.mp4";
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 5); // A grid: 0,5,10,15,… (nearest to 12 is 10)
        probe.GatedPaths.Add(path);

        var row = new BulkItemViewModel(path, probe, Gate()) { Duration = TimeSpan.FromSeconds(60) };
        row.IntroEnd.Requested = TimeSpan.FromSeconds(12);

        var scan1 = row.StartKeyframeScanAsync();       // parks at the gate
        probe.CurrentScans.Should().Be(1, "the first scan is parked at the gate");

        probe.SetUniform(path, TimeSpan.FromSeconds(60), 4); // B grid: 0,4,8,12,… (12 is an exact keyframe)
        var scan2 = row.StartKeyframeScanAsync();       // supersedes scan1 (cancels its CTS)
        probe.PeakScans.Should().Be(2, "the second scan started while the first was still in flight");

        probe.ReleaseScans();
        await scan2;
        try { await scan1; } catch { /* the superseded scan's cancellation is swallowed by the VM */ }

        row.KeyframesReady.Should().BeTrue();
        row.Keyframes.Should().Contain(TimeSpan.FromSeconds(12), "the second (B-grid) scan committed");
        row.Keyframes.Should().NotContain(TimeSpan.FromSeconds(5), "the first (A-grid) scan's result was discarded");
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12),
            "the handle re-snapped against the SECOND scan's keyframes (12), not the first's (would be 10)");
    }

    // ---- I15: scan throws → identity fallback + KeyframesReady -------------------------------

    // SPEC-011#I15 — a keyframe scan that throws leaves Keyframes empty, clears IsIndexingKeyframes, and
    // resolves the handles to identity snaps (Requested == Snapped); the row still becomes KeyframesReady.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task KeyframeScan_Throws_LeavesEmptyKeyframes_IdentitySnap_StillReady()
    {
        var probe = new ThrowingKeyframeProbe();
        const string path = @"C:\v\ep.mp4";
        var row = new BulkItemViewModel(path, probe, Gate()) { Duration = TimeSpan.FromSeconds(60) };
        row.IntroEnd.Requested = TimeSpan.FromSeconds(12);

        await row.StartKeyframeScanAsync();

        row.Keyframes.Should().BeEmpty("a thrown scan leaves the keyframe list empty");
        row.KeyframesReady.Should().BeTrue("the row still resolves (indexing flag cleared) despite the scan error");
        row.IsIndexingKeyframes.Should().BeFalse();
        row.IntroEnd.IsSnapPending.Should().BeFalse();
        row.IntroEnd.Snapped.Should().Be(row.IntroEnd.Requested, "no keyframes → identity snap (Requested == Snapped)");
    }

    // ---- I16: Warning notes -----------------------------------------------------------------

    // SPEC-011#I16 — the computed 'coarse keyframes' note (avg GOP > 4s).
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Warning_CoarseGop_Note()
    {
        var probe = new BulkFakeProbe();
        var row = await ReadyRowAsync(probe, @"C:\v\coarse.mp4", 60, 6, introSeconds: 0); // 6s GOP > 4s threshold

        row.Warning.Should().Contain("coarse keyframes", "a mean GOP over 4s raises the coarse-keyframes note");
    }

    // SPEC-011#I16 — the 'nothing trimmed from the tail' note (outro ≈ EOF with a real intro).
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Warning_OutroAtEof_Note()
    {
        var probe = new BulkFakeProbe();
        // Intro 10 (real), outro snapped to EOF (60) → the tail keeps everything.
        var row = await ReadyRowAsync(probe, @"C:\v\tail.mp4", 60, 2, introSeconds: 10, outroSeconds: 60);

        row.Warning.Should().Contain("nothing trimmed from the tail",
            "an outro that snaps to EOF (with a real intro) raises the tail note");
    }

    // SPEC-011#I16 — the 'very short keep' note (0 < kept < MinKeptSpan).
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Warning_VeryShortKeep_Note()
    {
        var probe = new BulkFakeProbe();
        // 0.7s GOP → MinKeptSpan = max(1s, 0.7s) = 1s. Intro 7.0 → outro 7.7 → kept 0.7s ∈ (0.5s, 1s).
        var row = await ReadyRowAsync(probe, @"C:\v\short.mp4", 60, 0.7, introSeconds: 7.0, outroSeconds: 7.7);

        row.Warning.Should().Contain("very short keep",
            "a kept span above the boundary epsilon but below MinKeptSpan raises the very-short-keep note");
    }

    // ---- I20: ApplyToAll no-op guards -------------------------------------------------------

    // SPEC-011#I20 — BulkCutViewModel.ApplyToAll returns null and mutates nothing when the source is null
    // or not KeyframesReady (still indexing) / has no probed Duration.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAll_NullOrNotReadySource_ReturnsNull_MutatesNothing()
    {
        var (vm, probe, _) = BuildVm();
        probe.SetUniform(@"C:\v\a.mp4", TimeSpan.FromSeconds(60), 2);
        probe.SetUniform(@"C:\v\b.mp4", TimeSpan.FromSeconds(60), 2);
        probe.GatedPaths.Add(@"C:\v\a.mp4"); // a's scan stays open → a is still indexing (not ready)

        await vm.AddFilesAsync(new[] { @"C:\v\a.mp4", @"C:\v\b.mp4" });
        var stillIndexing = vm.Items.Single(i => i.Path == @"C:\v\a.mp4");
        var target = vm.Items.Single(i => i.Path == @"C:\v\b.mp4");
        target.IntroEnd.Requested = TimeSpan.FromSeconds(6);

        stillIndexing.KeyframesReady.Should().BeFalse("precondition: source is still indexing");
        var targetIntroBefore = target.IntroEnd.Requested;

        vm.ApplyToAll(null).Should().BeNull("a null source is a no-op");
        vm.ApplyToAll(stillIndexing).Should().BeNull("a not-ready source is a no-op");
        target.IntroEnd.Requested.Should().Be(targetIntroBefore, "no target row was mutated");

        probe.ReleaseScans();
        await stillIndexing.CurrentScanTask;
    }

    // ---- I22: ApplyToAll clears outro when source has none ----------------------------------

    // SPEC-011#I22 — when the SOURCE row has no outro, every applied target has its outro CLEARED so it
    // mirrors the source's keep-to-EOF shape.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAll_SourceHasNoOutro_ClearsTargetOutro()
    {
        var (vm, probe, _) = BuildVm();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);              // NO outro
        var target = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4, outroSeconds: 80); // HAS outro

        source.HasOutro.Should().BeFalse("precondition: the source keeps to EOF");
        target.HasOutro.Should().BeTrue("precondition: the target has an outro");

        vm.ApplyToAll(source);

        target.HasOutro.Should().BeFalse("a no-outro source clears every applied target's outro (mirror keep-to-EOF)");
    }

    // SPEC-011#I22 — the per-TARGET filter: only rows that are IsCheckedByUser AND KeyframesReady AND
    // have a probed Duration are applied to; the source row itself is skipped. An unticked row and a
    // still-indexing row are both left exactly as they were.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAll_SkipsTheSource_UncheckedRows_AndStillIndexingRows()
    {
        var (vm, probe, _) = BuildVm();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 12);

        var unticked = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 4);
        unticked.IsEnabled = false; // the user unticked the checkbox → IsCheckedByUser is false

        // c's keyframe scan stays parked at the gate → probed (has a Duration) but NOT KeyframesReady.
        probe.SetUniform(@"C:\v\c.mp4", TimeSpan.FromSeconds(60), 2);
        probe.GatedPaths.Add(@"C:\v\c.mp4");
        await vm.AddFilesAsync(new[] { @"C:\v\c.mp4" });
        var stillIndexing = vm.Items.Single(i => i.Path == @"C:\v\c.mp4");

        var eligible = await AddRowAsync(vm, probe, @"C:\v\d.mp4", 60, 2, introSeconds: 4);

        unticked.IsCheckedByUser.Should().BeFalse("precondition: b carries the raw unticked intent");
        stillIndexing.KeyframesReady.Should().BeFalse("precondition: c is still indexing");
        var untickedIntroBefore = unticked.IntroEnd.Requested;

        var report = vm.ApplyToAll(source);

        report!.AppliedCount.Should().Be(
            1, "only the checked, keyframes-ready, probed target is eligible — the other two are filtered out");
        eligible.IntroEnd.Requested.Should().Be(
            TimeSpan.FromSeconds(12), "the one eligible target took the source's requested intro");
        unticked.IntroEnd.Requested.Should().Be(untickedIntroBefore, "an unchecked row is never touched");
        stillIndexing.IntroEnd.Requested.Should().Be(TimeSpan.Zero, "a still-indexing row is never touched");
        source.IntroEnd.Requested.Should().Be(
            TimeSpan.FromSeconds(12), "the source row itself is skipped, never re-applied to");

        probe.ReleaseScans();
        await stillIndexing.CurrentScanTask;
    }

    // ---- I24: ApplyToAllCommand.CanExecute needs > 1 row ------------------------------------

    // SPEC-011#I24 — ApplyToAllCommand.CanExecute is true only when Items.Count > 1.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAllCommand_CanExecute_NeedsMoreThanOneRow()
    {
        var (vm, probe, _) = BuildVm();

        vm.ApplyToAllCommand.CanExecute(null).Should().BeFalse("0 rows → nothing to apply across");

        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);
        vm.ApplyToAllCommand.CanExecute(null).Should().BeFalse("1 row → no other row to apply to");

        var second = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 10);
        vm.ApplyToAllCommand.CanExecute(second).Should().BeTrue("2 rows → apply-to-all is enabled");
    }

    // ---- I43: late post after a terminal batch state is ignored -----------------------------

    // SPEC-011#I43 — a late MarkRunning / SetProgress after a row reached a terminal batch state (Done)
    // is ignored: it never re-animates or overrides the terminal RowState / Progress.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task LatePost_AfterTerminalDone_IsIgnored()
    {
        var probe = new BulkFakeProbe();
        var row = await ReadyRowAsync(probe, @"C:\v\ep.mp4", 60, 2, introSeconds: 10, outroSeconds: 50);
        row.IsValidCut.Should().BeTrue("precondition: a real, valid trim");

        // Drive the row to a terminal Done via the ledger fan-out.
        var item = row.BuildBulkTrimItem();
        row.ApplyResult(new BulkTrimItemResult(item, ItemOutcome.Done, @"C:\v\ep_trimmed.mp4", null, Array.Empty<string>()));
        row.RowState.Should().Be(RowState.Done);
        row.Progress.Should().Be(1.0);

        // A late MarkRunning / SetProgress must not override the terminal state (guards the D-004 hazard).
        row.MarkRunning();
        row.SetProgress(0.3);

        row.RowState.Should().Be(RowState.Done, "a late MarkRunning after a terminal state is ignored");
        row.Progress.Should().Be(1.0, "a late SetProgress after a terminal state is ignored");
    }

    // ---- I2: an unprobeable source becomes a LoadFailed row, excluded from the batch ---------

    // SPEC-011#I2 — PopulateAsync's failed-ProbeResult branch marks the row LoadFailed: it is auto-
    // disabled, its indexing flag is cleared (it never spins), it does not hold the run gate hostage,
    // and the engine's batch input never contains it.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AddFiles_UnprobeableFile_BecomesLoadFailedRow_ExcludedFromTheBatch()
    {
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine);

        probe.FailProbePaths.Add(@"C:\v\bad.mp4"); // ProbeAsync returns ProbeResult.Failure
        probe.SetUniform(@"C:\v\good.mp4", TimeSpan.FromSeconds(60), 2);

        await vm.AddFilesAsync(new[] { @"C:\v\bad.mp4", @"C:\v\good.mp4" });

        var bad = vm.Items.Single(i => i.Path == @"C:\v\bad.mp4");
        var good = vm.Items.Single(i => i.Path == @"C:\v\good.mp4");
        good.IntroEnd.Requested = TimeSpan.FromSeconds(10);

        bad.RowState.Should().Be(RowState.LoadFailed, "a failed probe marks the row LoadFailed");
        bad.IsEnabled.Should().BeFalse("a LoadFailed row is auto-disabled — it can never be ticked into a batch");
        bad.IsIndexingKeyframes.Should().BeFalse(
            "the failed row clears its indexing flag instead of spinning on a scan that will never run");

        vm.CanRunBatch.Should().BeTrue("the excluded row must not hold the run gate hostage");
        vm.RunLabel.Should().Be("Run bulk cut (1)", "only the probed row counts toward the batch");

        await vm.RunBatchAsync();

        engine.ReceivedItems.Should()
            .ContainSingle("the unprobeable row is excluded from the batch handed to the engine")
            .Which.InputPath.Should().Be(@"C:\v\good.mp4");
    }
}
