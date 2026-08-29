using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-128 (SPEC-011) — the bulk selection gesture: <c>Select all</c> / <c>Select none</c> over every row,
/// mirroring the Split screen's <c>SetAllSegmentsSelected</c>. Two things make this more than a loop.
///
/// <para><b>It writes INTENT, not eligibility.</b> T-127 split the row's one conflated flag into the user's
/// <see cref="BulkItemViewModel.IsCheckedByUser"/> intent and the app's computed
/// <see cref="BulkItemViewModel.IsEnabled"/> eligibility. <see cref="BulkCutViewModel.SetAllItemsChecked"/>
/// writes the former, so ticking rows that have no cut yet is a legal, meaningful gesture: the intent is
/// remembered and the row joins the batch the moment a real cut makes it eligible. Writing the computed
/// side instead would silently do nothing on exactly those rows (the T-127 bug), so these tests pin the
/// split rather than just the checkbox.</para>
///
/// <para><b>It must stay cheap and must not go stale.</b> The write is pure VM state — no probe, no keyframe
/// re-scan, no thumbnail grab — and the batch-level projections (<c>CanRunBatch</c> / <c>RunLabel</c>) are
/// refreshed for the whole write instead of being left behind. Performance is asserted STRUCTURALLY
/// (bounded heavy-op counts + a linear notification bound), never by wall-clock timing.</para>
/// </summary>
public sealed class BulkSelectAllTests
{
    private const double DurationSeconds = 60;
    private const double GridSeconds = 2; // 2s keyframe grid ⇒ AverageGop 2s ⇒ MinKeptSpan 2s

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, ThrowingFakeSplitEngine Split, FakeBulkTrimEngine Engine) Build()
    {
        var probe = new BulkFakeProbe();
        var split = new ThrowingFakeSplitEngine();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(probe, split, new FakeThumbnailService(), new FakeSettings(), engine);
        return (vm, probe, split, engine);
    }

    /// <summary>
    /// Build variant that hands back the thumbnail fake AND makes its debounce wait immediate, so cut-point
    /// frame grabs really do run during setup. That is what turns the perf assertion from vacuous ("0 grabs
    /// before, 0 after") into a real one ("N grabs before, still exactly N after the bulk write").
    /// </summary>
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeThumbnailService Thumbs, FakeBulkTrimEngine Engine) BuildWithLiveThumbnails()
    {
        var probe = new BulkFakeProbe();
        var thumbs = new FakeThumbnailService { ThumbnailFactory = (p, t, w) => $"{p}@{t}x{w}.png" };
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe,
            new ThrowingFakeSplitEngine(),
            thumbs,
            new FakeSettings(),
            engine,
            thumbnailDebounce: TimeSpan.FromMilliseconds(1),
            thumbnailDelay: (_, _) => Task.CompletedTask); // immediate ⇒ the grab runs inline, deterministically
        return (vm, probe, thumbs, engine);
    }

    /// <summary>Add one 60s/2s-grid row; <paramref name="introSeconds"/> null ⇒ leave it a no-op trim (no cut set).</summary>
    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double? introSeconds = null)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(DurationSeconds), GridSeconds);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        if (introSeconds is double s)
        {
            row.IntroEnd.Requested = TimeSpan.FromSeconds(s);
        }

        return row;
    }

    private static async Task<List<BulkItemViewModel>> AddRowsAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, int count, double? introSeconds = null)
    {
        var rows = new List<BulkItemViewModel>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(await AddRowAsync(vm, probe, $@"C:\v\ep{i:00}.mp4", introSeconds));
        }

        return rows;
    }

    // ---- 1. The gesture itself ----------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectNone_UnticksEveryRow_AndSelectAll_TicksThemAllBack()
    {
        var (vm, probe, split, engine) = Build();
        var rows = await AddRowsAsync(vm, probe, 4, introSeconds: 6);

        rows.Should().OnlyContain(r => r.IsCheckedByUser, "rows start included — that is the import default");

        vm.SelectNoItemsCommand.Execute(null);

        rows.Should().OnlyContain(r => !r.IsCheckedByUser, "select none unticks EVERY row, not just the selected one");

        vm.SelectAllItemsCommand.Execute(null);

        rows.Should().OnlyContain(r => r.IsCheckedByUser, "select all ticks every row in one gesture");

        // The gesture is pure VM state: it never reaches the split engine or the batch engine.
        split.WasCalled.Should().BeFalse();
        engine.CallCount.Should().Be(0);
    }

    // ---- 2. It writes INTENT, not eligibility (the T-127 split) --------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectAll_OnRowsWithNoCut_WritesIntent_WhileEligibilityStaysFalse()
    {
        var (vm, probe, _, _) = Build();
        var rows = await AddRowsAsync(vm, probe, 3); // no cut set ⇒ every row is a no-op trim

        vm.SelectNoItemsCommand.Execute(null); // so the select-all below is a REAL write, not a no-op
        vm.SelectAllItemsCommand.Execute(null);

        rows.Should().OnlyContain(
            r => r.IsCheckedByUser,
            "the user's intent is recorded even on rows the app currently excludes — the whole point of T-127");
        rows.Should().OnlyContain(
            r => !r.IsEnabled,
            "eligibility is computed, not written: a no-op trim is still not runnable just because it is ticked");
        rows.Should().OnlyContain(
            r => r.IsExcludedDespiteBeingChecked,
            "ticked-but-excluded is a real, nameable state — never a silently dead checkbox");
        rows.Should().OnlyContain(
            r => r.ExclusionReason != null && r.ExclusionReason.Contains("nothing to trim yet", StringComparison.Ordinal),
            "…and the row says WHY it is not counted");

        vm.CanRunBatch.Should().BeFalse("intent alone does not make a batch runnable");
        vm.RunLabel.Should().Be("Run bulk cut (0)");

        // The documented pay-off: the remembered intent is what lets a row join the batch the moment it
        // becomes eligible — no second click needed.
        rows[1].IntroEnd.Requested = TimeSpan.FromSeconds(6);

        rows[1].IsEnabled.Should().BeTrue("the row was already ticked, so a real cut is all it needed");
        rows[1].IsExcludedDespiteBeingChecked.Should().BeFalse();
        rows[1].ExclusionReason.Should().BeNull();
        rows[0].IsEnabled.Should().BeFalse("…and ONLY the row that got a cut becomes eligible");
        rows[2].IsEnabled.Should().BeFalse();
        vm.RunLabel.Should().Be("Run bulk cut (1)", "exactly one row is now enabled AND valid");
        vm.CanRunBatch.Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectNone_ExcludesEveryRow_EvenThoughEachHasAValidCut()
    {
        var (vm, probe, _, _) = Build();
        var rows = await AddRowsAsync(vm, probe, 3, introSeconds: 6);

        vm.RunLabel.Should().Be("Run bulk cut (3)", "precondition: all three rows are enabled and valid");

        vm.SelectNoItemsCommand.Execute(null);

        rows.Should().OnlyContain(r => !r.IsEnabled, "unticking withdraws intent, so no row is runnable");
        rows.Should().OnlyContain(
            r => !r.IsExcludedDespiteBeingChecked && r.ExclusionReason == null,
            "an UNTICKED row is not excluded-despite-being-checked — it needs no explanation line");
        vm.CanRunBatch.Should().BeFalse();
        vm.RunLabel.Should().Be("Run bulk cut (0)");
    }

    // ---- 3. Round trip ------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectNone_ThenSelectAll_RestoresEveryRowToIncluded()
    {
        var (vm, probe, _, _) = Build();
        var rows = await AddRowsAsync(vm, probe, 4, introSeconds: 6);

        vm.SelectNoItemsCommand.Execute(null);
        vm.SelectAllItemsCommand.Execute(null);

        rows.Should().OnlyContain(r => r.IsCheckedByUser && r.IsEnabled, "the round trip is lossless");
        vm.CanRunBatch.Should().BeTrue();
        vm.RunLabel.Should().Be("Run bulk cut (4)", "the batch is back to its pre-gesture size");
    }

    // ---- 4. Guards ----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void BothCommands_AreDisabled_OnAnEmptyList()
    {
        var (vm, _, _, _) = Build();

        vm.Items.Should().BeEmpty();
        vm.CanChangeSelection.Should().BeFalse();
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeFalse("there is nothing to select");
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeFalse("there is nothing to deselect");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BothCommands_BecomeEnabled_OnceARowExists()
    {
        var (vm, probe, _, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", introSeconds: 6);

        vm.CanChangeSelection.Should().BeTrue();
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeTrue();
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BothCommands_AreDisabled_WhileTheBatchIsRunning()
    {
        var (vm, probe, _, engine) = Build();
        await AddRowsAsync(vm, probe, 3, introSeconds: 6);
        vm.CanRunBatch.Should().BeTrue("precondition: the batch is runnable");

        // Observe the guards MID-RUN — BeforeReturn fires inside the engine call, while the aggregate
        // operation is still in flight (the same seam BulkCutViewModelTests uses to watch rows run).
        bool runningObserved = false, canChange = true, canSelectAll = true, canSelectNone = true;
        engine.BeforeReturn = () =>
        {
            runningObserved = vm.Operation.IsRunning;
            canChange = vm.CanChangeSelection;
            canSelectAll = vm.SelectAllItemsCommand.CanExecute(null);
            canSelectNone = vm.SelectNoItemsCommand.CanExecute(null);
        };

        await vm.RunBatchAsync();

        runningObserved.Should().BeTrue("precondition: the observation really happened mid-run");
        canChange.Should().BeFalse("changing batch membership mid-run would desync the running set");
        canSelectAll.Should().BeFalse();
        canSelectNone.Should().BeFalse();

        vm.CanChangeSelection.Should().BeTrue("…and the gesture comes back once the run is over");
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeTrue();
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeTrue();
    }

    // ---- 5. Composition with apply-to-all (which targets INTENT) ------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectNone_ThenApplyToAll_AppliesToNothing()
    {
        var (vm, probe, _, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\src.mp4", introSeconds: 10);
        var targets = new[]
        {
            await AddRowAsync(vm, probe, @"C:\v\t1.mp4", introSeconds: 4),
            await AddRowAsync(vm, probe, @"C:\v\t2.mp4", introSeconds: 4),
            await AddRowAsync(vm, probe, @"C:\v\t3.mp4", introSeconds: 4),
        };

        vm.SelectNoItemsCommand.Execute(null);

        var report = vm.ApplyToAll(source);

        report.Should().NotBeNull("the source itself is still ready, so the gesture runs — it just has no targets");
        report!.AppliedCount.Should().Be(0, "apply-to-all filters on IsCheckedByUser, which select-none just cleared");
        report.InvalidatedRows.Should().BeEmpty();
        targets.Should().OnlyContain(
            r => r.IntroEnd.Snapped == TimeSpan.FromSeconds(4),
            "an unticked row keeps its own cut — nothing was copied onto it");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectAll_ThenApplyToAll_AppliesToEveryOtherRow()
    {
        var (vm, probe, _, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\src.mp4", introSeconds: 10);
        var targets = new[]
        {
            await AddRowAsync(vm, probe, @"C:\v\t1.mp4", introSeconds: 4),
            await AddRowAsync(vm, probe, @"C:\v\t2.mp4", introSeconds: 4),
            await AddRowAsync(vm, probe, @"C:\v\t3.mp4", introSeconds: 4),
        };

        vm.SelectNoItemsCommand.Execute(null);
        vm.SelectAllItemsCommand.Execute(null);

        var report = vm.ApplyToAll(source);

        report!.AppliedCount.Should().Be(3, "re-ticking restored every row as an apply-to-all target");
        report.InvalidatedRows.Should().BeEmpty();
        targets.Should().OnlyContain(
            r => r.IntroEnd.Snapped == TimeSpan.FromSeconds(10),
            "the source's intro was copied ABSOLUTE onto each re-included row");
        source.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10), "the source is never a target of itself");
        vm.RunLabel.Should().Be("Run bulk cut (4)");
    }

    // ---- 6. Performance (structural — bounded heavy ops + linear notification cost) ------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BulkSelection_IsPureVmState_NoKeyframeReScan_NoThumbnailGrab()
    {
        const int RowCount = 12;
        var (vm, probe, thumbs, engine) = BuildWithLiveThumbnails();
        await AddRowsAsync(vm, probe, RowCount, introSeconds: 6);

        var scansBefore = probe.GetKeyframesCallCount;
        var grabsBefore = thumbs.GetThumbnailCallCount;

        scansBefore.Should().Be(RowCount, "precondition: each row scanned its keyframes exactly once");
        grabsBefore.Should().BeGreaterThan(
            0, "precondition: the harness really is grabbing cut-point frames, so 'unchanged' below means something");

        vm.SelectNoItemsCommand.Execute(null);
        vm.SelectAllItemsCommand.Execute(null);
        vm.SelectNoItemsCommand.Execute(null);
        vm.SelectAllItemsCommand.Execute(null);

        // PERF (no I/O on the path — structural, call-count based): the bulk write only flips a bool per
        // row. Four full sweeps over 12 rows must not touch ffprobe or ffmpeg even once.
        probe.GetKeyframesCallCount.Should().Be(
            scansBefore, "toggling checkbox intent must never re-scan keyframes");
        thumbs.GetThumbnailCallCount.Should().Be(
            grabsBefore, "toggling checkbox intent must never re-grab a cut-point frame");
        engine.CallCount.Should().Be(0, "…and it certainly never runs the batch");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BulkSelection_RefreshesTheBatchProjection_AtLinearCost()
    {
        const int RowCount = 12;
        var (vm, probe, _, _) = Build();
        var rows = await AddRowsAsync(vm, probe, RowCount, introSeconds: 6);

        vm.SelectNoItemsCommand.Execute(null); // set up the state the measured write has to undo

        var canRunNotifications = 0;
        var runLabelNotifications = 0;
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BulkCutViewModel.CanRunBatch))
            {
                canRunNotifications++;
            }
            else if (e.PropertyName == nameof(BulkCutViewModel.RunLabel))
            {
                runLabelNotifications++;
            }
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            vm.SelectAllItemsCommand.Execute(null);
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }

        // NOT STALE: the batch-level projections really were re-raised for this write, so a bound button
        // re-queries instead of showing the pre-gesture count.
        canRunNotifications.Should().BeGreaterThan(0, "CanRunBatch must be re-raised, not left stale");
        runLabelNotifications.Should().BeGreaterThan(0, "RunLabel must be re-raised, not left stale");

        // PERF (O(N), not O(N²) — structural, notification-count based): each row raises its own change and
        // the write refreshes the batch projections once more at the end, so the cost is a small constant
        // per row (~2N + 1). A refresh that re-projected the whole list per row would be 12² = 144 raises
        // and would blow this linear cap.
        var linearCap = 4 * RowCount;
        canRunNotifications.Should().BeLessThanOrEqualTo(
            linearCap, "a bulk selection must cost O(N) batch refreshes, never O(N²)");
        runLabelNotifications.Should().BeLessThanOrEqualTo(linearCap, "same linear bound for the count-aware label");

        // CORRECTNESS: the projection those notifications advertised is the right one.
        rows.Should().OnlyContain(r => r.IsEnabled);
        vm.CanRunBatch.Should().BeTrue();
        vm.RunLabel.Should().Be($"Run bulk cut ({RowCount})");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BulkSelection_TouchesOnlyLiveRows_ARemovedRowIsLeftAlone()
    {
        var (vm, probe, _, _) = Build();
        var rows = await AddRowsAsync(vm, probe, 3, introSeconds: 6);
        var removed = rows[1];

        vm.RemoveCommand.Execute(removed);

        vm.SelectNoItemsCommand.Execute(null);

        removed.IsCheckedByUser.Should().BeTrue(
            "the write walks Items only — a row that left the list is out of scope, never silently mutated");
        vm.Items.Should().OnlyContain(r => !r.IsCheckedByUser, "…while every remaining row was unticked");
        vm.RunLabel.Should().Be("Run bulk cut (0)");
    }
}
