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
///
/// <para><b>The gate must be ANNOUNCED, not merely computed.</b> SPEC-011 I102 states TWO things: both
/// commands are gated by <see cref="BulkCutViewModel.CanChangeSelection"/>, AND <c>RaiseRunState</c>
/// re-raises that property together with both commands' own <see cref="RelayCommand.CanExecuteChanged"/>,
/// so an add, a remove, a Clear and a run's start/end re-evaluate the buttons deterministically. Reading
/// the gate can only ever confirm the first half: strip the three notification lines out of
/// <c>RaiseRunState</c> and every polling assertion below stays green while the real WPF buttons go stale
/// (they would then re-query only when WPF's heuristic, weak-referenced global requery happened to fire on
/// unrelated input — the T-111 staleness bug). The tests in §4b therefore SUBSCRIBE to the three
/// notifications rather than polling the gate.</para>
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

    /// <summary>
    /// Subscribes to the three notifications SPEC-011 I102's second clause names — the
    /// <see cref="BulkCutViewModel.CanChangeSelection"/> property change and BOTH commands' own
    /// <see cref="RelayCommand.CanExecuteChanged"/> — and counts each one. This is the only way to observe
    /// the clause at all: <c>vm.CanChangeSelection</c> and <c>Command.CanExecute(null)</c> recompute from
    /// scratch on every read, so they report the right answer even when nothing was ever published.
    ///
    /// <para>All three are raised in one unconditional block at the end of <c>RaiseRunState</c> and are
    /// raised NOWHERE else, so their counts move in LOCKSTEP by construction. Asserting that equality is
    /// what makes a single deleted line detectable: drop the property raise and
    /// <see cref="CanChangeRaises"/> falls to 0; drop either command's re-raise and only that counter
    /// falls out of step.</para>
    ///
    /// <para><see cref="Dispose"/> detaches before the assertions read the counters, so no late
    /// continuation can move one mid-assert (the same discipline as the try/finally around
    /// <c>PropertyChanged</c> in the perf tests below).</para>
    /// </summary>
    private sealed class GateWatch : IDisposable
    {
        private readonly BulkCutViewModel _vm;

        public GateWatch(BulkCutViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnVmChanged;
            _vm.SelectAllItemsCommand.CanExecuteChanged += OnSelectAllCanExecuteChanged;
            _vm.SelectNoItemsCommand.CanExecuteChanged += OnSelectNoneCanExecuteChanged;
        }

        /// <summary>How many times <c>PropertyChanged(nameof(CanChangeSelection))</c> was published.</summary>
        public int CanChangeRaises { get; private set; }

        /// <summary>How many times <c>SelectAllItemsCommand</c> raised its OWN CanExecuteChanged.</summary>
        public int SelectAllRaises { get; private set; }

        /// <summary>How many times <c>SelectNoItemsCommand</c> raised its OWN CanExecuteChanged.</summary>
        public int SelectNoneRaises { get; private set; }

        public void Dispose()
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.SelectAllItemsCommand.CanExecuteChanged -= OnSelectAllCanExecuteChanged;
            _vm.SelectNoItemsCommand.CanExecuteChanged -= OnSelectNoneCanExecuteChanged;
        }

        private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BulkCutViewModel.CanChangeSelection))
            {
                CanChangeRaises++;
            }
        }

        private void OnSelectAllCanExecuteChanged(object? sender, EventArgs e) => SelectAllRaises++;

        private void OnSelectNoneCanExecuteChanged(object? sender, EventArgs e) => SelectNoneRaises++;
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

    // ---- 4b. The gate must be ANNOUNCED, not merely computed (I102, second clause) --------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AddingTheFirstRow_RaisesTheGate_AndBothCommandsCanExecuteChanged()
    {
        var (vm, probe, split, engine) = Build();

        vm.CanChangeSelection.Should().BeFalse("precondition: an empty list gates both gestures off");

        // No cut on the row: the gate is Items.Count > 0, so the add ALONE is what flips it — nothing else
        // in the measured window can account for the raises.
        var watch = new GateWatch(vm);
        try
        {
            await AddRowAsync(vm, probe, @"C:\v\a.mp4");
        }
        finally
        {
            watch.Dispose();
        }

        // NOT STALE: the gate flipped false→true and SAID SO. A bound WPF button re-queries only when it is
        // told to, so the raise — not the getter's value — is what makes "Select all" light up on first import.
        watch.CanChangeRaises.Should().BeGreaterThan(
            0,
            "adding the first row enables both gestures, and only a PropertyChanged tells a bound button so — " +
            "polling CanChangeSelection afterwards cannot distinguish 'announced' from 'silently true'");
        watch.SelectAllRaises.Should().Be(
            watch.CanChangeRaises,
            "Select all's OWN CanExecuteChanged is re-raised in the same pass — the three notifications are " +
            "published together, so any count that falls out of step means one of them was dropped");
        watch.SelectNoneRaises.Should().Be(watch.CanChangeRaises, "…and Select none's, in that same pass");

        // CORRECTNESS: what was announced is true — and true even though the row has no cut yet, because
        // the gesture writes INTENT (§2) and intent is legal on a not-yet-eligible row.
        vm.CanChangeSelection.Should().BeTrue();
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeTrue();
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeTrue();

        // PERF (structural — bounded heavy ops, no I/O beyond the import's own scan): announcing a gate is
        // pure notification work. One row in, exactly one keyframe scan, and neither engine touched.
        probe.GetKeyframesCallCount.Should().Be(
            1, "the row scanned its keyframes once at import — raising the gate adds none");
        engine.CallCount.Should().Be(0);
        split.WasCalled.Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARunStartingAndEnding_ReRaisesTheGate_AndBothCommandsCanExecuteChanged()
    {
        const int RowCount = 3;
        var (vm, probe, split, engine) = Build();
        await AddRowsAsync(vm, probe, RowCount, introSeconds: 6);
        vm.CanRunBatch.Should().BeTrue("precondition: the batch is runnable");

        var scansBefore = probe.GetKeyframesCallCount;
        var watch = new GateWatch(vm);
        int canChangeAtRunStart = -1, selectAllAtRunStart = -1, selectNoneAtRunStart = -1;
        var canChangeAtRunEnd = -1;

        // Snapshot MID-RUN through the same BeforeReturn seam BothCommands_AreDisabled_WhileTheBatchIsRunning
        // uses: it fires inside the engine call, after the aggregate op has already flipped IsRunning true, so
        // whatever these counters hold was published by the run STARTING.
        engine.BeforeReturn = () =>
        {
            canChangeAtRunStart = watch.CanChangeRaises;
            selectAllAtRunStart = watch.SelectAllRaises;
            selectNoneAtRunStart = watch.SelectNoneRaises;
        };

        // …and again the instant the op leaves Running. The VM subscribed to Operation.PropertyChanged in its
        // constructor, so its own handler has ALREADY run for this very event by the time ours does — the
        // reading therefore includes the run-END re-raise and nothing that happens later in the tear-down.
        void OnOperationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OperationViewModel.IsRunning)
                && !vm.Operation.IsRunning
                && canChangeAtRunStart >= 0
                && canChangeAtRunEnd < 0)
            {
                canChangeAtRunEnd = watch.CanChangeRaises;
            }
        }

        vm.Operation.PropertyChanged += OnOperationChanged;
        try
        {
            await vm.RunBatchAsync();
        }
        finally
        {
            vm.Operation.PropertyChanged -= OnOperationChanged;
            watch.Dispose();
        }

        // START — the buttons were TOLD they had just been gated off. The polling test can see that
        // CanChangeSelection reads false mid-run; it cannot see whether anything ever published that.
        canChangeAtRunStart.Should().BeGreaterThan(
            0,
            "a run starting disables both gestures, and a bound button greys out only when it is told to " +
            "re-query — without the raise it stays clickable until the global requery happens to fire");
        selectAllAtRunStart.Should().Be(canChangeAtRunStart, "Select all's own CanExecuteChanged rides the same pass");
        selectNoneAtRunStart.Should().Be(canChangeAtRunStart, "…and so does Select none's");

        // END — and told again the moment the run handed the gestures back.
        canChangeAtRunEnd.Should().BeGreaterThan(
            canChangeAtRunStart,
            "leaving Running re-enables both gestures, so the run's END must publish a fresh raise of its own — " +
            "a button greyed out at start would otherwise never learn it may light up again");
        watch.SelectAllRaises.Should().Be(
            watch.CanChangeRaises, "the three notifications stay in lockstep across the whole run");
        watch.SelectNoneRaises.Should().Be(watch.CanChangeRaises);

        // CORRECTNESS: the state those notifications advertised at each end.
        vm.Operation.IsRunning.Should().BeFalse();
        vm.CanChangeSelection.Should().BeTrue("the gesture is back once the run is over");
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeTrue();
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeTrue();

        // PERF (structural — bounded heavy ops): the whole batch is ONE engine call, and re-raising the gates
        // around it costs no ffprobe work at all.
        engine.CallCount.Should().Be(1, "the run delegates to the batch engine exactly once");
        probe.GetKeyframesCallCount.Should().Be(scansBefore, "running a batch must never re-scan keyframes");
        split.WasCalled.Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RemovingTheLastRow_ReRaisesTheGate_AndBothCommandsCanExecuteChanged()
    {
        var (vm, probe, split, engine) = Build();
        var only = await AddRowAsync(vm, probe, @"C:\v\a.mp4", introSeconds: 6);

        vm.CanChangeSelection.Should().BeTrue("precondition: one row is enough to enable both gestures");
        var scansBefore = probe.GetKeyframesCallCount;

        var watch = new GateWatch(vm);
        try
        {
            vm.RemoveCommand.Execute(only);
        }
        finally
        {
            watch.Dispose();
        }

        vm.Items.Should().BeEmpty("precondition: that was the LAST row");

        // I102 names removes as a trigger. BulkSelection_TouchesOnlyLiveRows removes 1 of 3 and asserts
        // nothing about the gates at all, so this is the first test that can fail on a dropped raise.
        watch.CanChangeRaises.Should().BeGreaterThan(
            0,
            "emptying the list gates both gestures off, and a bound button greys out only when told to re-query");
        watch.SelectAllRaises.Should().Be(
            watch.CanChangeRaises, "Select all's own CanExecuteChanged rides the same pass");
        watch.SelectNoneRaises.Should().Be(watch.CanChangeRaises, "…and so does Select none's");

        // CORRECTNESS: what was announced is true.
        vm.CanChangeSelection.Should().BeFalse("there is nothing left to select");
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeFalse();
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeFalse();

        // PERF (structural — no I/O on the path): dropping a row is list + notification work only.
        probe.GetKeyframesCallCount.Should().Be(scansBefore, "removing a row must never re-scan keyframes");
        engine.CallCount.Should().Be(0);
        split.WasCalled.Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ClearingEveryRow_ReRaisesTheGate_AndBothCommandsCanExecuteChanged()
    {
        const int RowCount = 3;
        var (vm, probe, split, engine) = Build();
        await AddRowsAsync(vm, probe, RowCount, introSeconds: 6);

        vm.CanChangeSelection.Should().BeTrue("precondition: three rows, no run ⇒ both gestures enabled");
        var scansBefore = probe.GetKeyframesCallCount;

        var watch = new GateWatch(vm);
        try
        {
            vm.ClearCommand.Execute(null);
        }
        finally
        {
            watch.Dispose();
        }

        vm.Items.Should().BeEmpty();
        watch.CanChangeRaises.Should().BeGreaterThan(
            0,
            "Clear is the third trigger I102 names — it empties the list, so both gestures must be published off");
        watch.SelectAllRaises.Should().Be(
            watch.CanChangeRaises, "Select all's own CanExecuteChanged rides the same pass");
        watch.SelectNoneRaises.Should().Be(watch.CanChangeRaises, "…and so does Select none's");

        // CORRECTNESS: what was announced is true.
        vm.CanChangeSelection.Should().BeFalse();
        vm.SelectAllItemsCommand.CanExecute(null).Should().BeFalse();
        vm.SelectNoItemsCommand.CanExecute(null).Should().BeFalse();

        // PERF (structural — no I/O on the path): Clear cancels scans and drops rows; it starts none.
        probe.GetKeyframesCallCount.Should().Be(scansBefore, "clearing must never re-scan keyframes");
        engine.CallCount.Should().Be(0);
        split.WasCalled.Should().BeFalse();
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
        //
        // PERF — EXACTLY ONE refresh, not "at most O(N)". The earlier version of this test capped the raise
        // count at 4N and could not fail on the regression it existed to catch: without the
        // _suspendRunStateRefresh guard in SetAllItemsChecked, each row's setter raises IsCheckedByUser AND
        // IsEnabled, both of which pass OnItemChanged's filter, giving 2N+1 = 25 raises for N=12 — comfortably
        // under a cap of 48, so the test stayed green while every raise re-ran CanRunBatch's two O(N) LINQ
        // passes and RunLabel's O(N) Count. Raise COUNT is linear by construction; the cost the invariant
        // guards is the per-raise GETTER work. Pinning the exact constant is what actually detects it.
        canRunNotifications.Should().Be(
            1,
            "the whole bulk write must publish the batch projection ONCE — one raise per row would re-run " +
            "CanRunBatch's O(N) scan N times over, which is the O(N²) stall the suspend guard exists to prevent");
        runLabelNotifications.Should().Be(1, "same single publish for the count-aware label");

        // CORRECTNESS: the projection those notifications advertised is the right one.
        rows.Should().OnlyContain(r => r.IsEnabled);
        vm.CanRunBatch.Should().BeTrue();
        vm.RunLabel.Should().Be($"Run bulk cut ({RowCount})");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SelectAll_OnAnAlreadyFullyTickedList_StillPublishesTheProjection_ExactlyOnce()
    {
        const int RowCount = 12;
        var (vm, probe, split, engine) = Build();
        var rows = await AddRowsAsync(vm, probe, RowCount, introSeconds: 6);

        // Deliberately NO select-none first. Every other select-all in this file is preceded by one, so the
        // measured write always had something to change; this is the case I101's second clause is about —
        // the refresh publishes "even when no row's value actually changed".
        rows.Should().OnlyContain(
            r => r.IsCheckedByUser, "precondition: every row is ALREADY ticked, so the write below changes nothing");
        var scansBefore = probe.GetKeyframesCallCount;

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

        // EXACTLY ONE, same idiom as BulkSelection_RefreshesTheBatchProjection_AtLinearCost — but this case
        // pins a different half of the invariant. Each row's IsCheckedByUser setter is equality-guarded, so
        // an all-ticked list makes every per-row raise vanish: the trailing RaiseRunState() is the ONLY thing
        // that can produce a notification here. 0 would mean the refresh was skipped as a "no-op write" and a
        // bound button kept whatever count it was last told; more than 1 would mean a per-row fan-out leaked
        // past the suspend guard, re-running CanRunBatch's O(N) scan once per row.
        canRunNotifications.Should().Be(
            1,
            "the trailing refresh is unconditional — a select-all over an already-all-ticked list must still " +
            "publish the batch projection exactly once, not zero times because nothing changed");
        runLabelNotifications.Should().Be(1, "same single publish for the count-aware label");

        // CORRECTNESS: an idempotent gesture leaves the list — and the projection it advertised — unchanged.
        rows.Should().OnlyContain(r => r.IsCheckedByUser && r.IsEnabled, "re-ticking a ticked list is a no-op");
        vm.CanRunBatch.Should().BeTrue();
        vm.RunLabel.Should().Be($"Run bulk cut ({RowCount})");

        // PERF (structural — no I/O on the path): a write that changes nothing must also COST nothing beyond
        // the one refresh — no re-scan, no frame grab path, no engine call.
        probe.GetKeyframesCallCount.Should().Be(scansBefore, "an idempotent select-all must never re-scan keyframes");
        engine.CallCount.Should().Be(0);
        split.WasCalled.Should().BeFalse();
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
