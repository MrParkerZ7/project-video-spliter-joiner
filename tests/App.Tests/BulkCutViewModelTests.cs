using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="BulkCutViewModel"/> (T-096): apply-to-all (outro-from-end + re-validate/report),
/// the CanRunBatch gate, DELEGATION to <see cref="IBulkTrimEngine.RunAsync"/> (proven by a
/// <see cref="ThrowingFakeSplitEngine"/> that must never be called), weighted-monotonic overall progress,
/// ledger routing by <c>Tag</c>, the Blocked/Cancelled paths, dedup, and the bounded keyframe-scan throttle.
/// </summary>
public sealed class BulkCutViewModelTests
{
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, ThrowingFakeSplitEngine Split, FakeBulkTrimEngine Engine) Build()
    {
        var probe = new BulkFakeProbe();
        var split = new ThrowingFakeSplitEngine();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(probe, split, new FakeThumbnailService(), new FakeSettings(), engine);
        return (vm, probe, split, engine);
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

    // ---- Apply-to-all -----------------------------------------------------------------------

    [Fact]
    public async Task ApplyToAll_CopiesIntroAbsolute_AndOutroFromEnd_ReSnapsEachTarget()
    {
        var (vm, probe, _, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10, outroSeconds: 50); // tail = 10
        var target = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);

        var report = vm.ApplyToAll(source);

        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10), "intro is copied ABSOLUTE (time-from-start)");
        target.HasOutro.Should().BeTrue();
        target.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(90), "outro is copied FROM END (100 − tail 10)");
        report!.AppliedCount.Should().Be(1);
        report.InvalidatedRows.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyToAll_ShorterTarget_IntroOvershoots_MarksInvalid_AndReportsIt()
    {
        var (vm, probe, _, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 80); // valid on the long source
        var target = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 6);

        var report = vm.ApplyToAll(source);

        target.IsValidCut.Should().BeFalse("intro 80 overshoots the 60s target");
        target.RowState.Should().Be(RowState.Invalid);
        report!.AppliedCount.Should().Be(1);
        report.InvalidatedRows.Should().Contain(target);
        vm.Items.Should().Contain(target, "an invalidated row is REPORTED, never dropped");
    }

    // ---- CanRunBatch gate -------------------------------------------------------------------

    [Fact]
    public async Task CanRunBatch_False_WhileAnEnabledRowStillIndexing()
    {
        var (vm, probe, _, _) = Build();
        probe.SetUniform(@"C:\v\a.mp4", TimeSpan.FromSeconds(60), 2);
        probe.SetUniform(@"C:\v\b.mp4", TimeSpan.FromSeconds(60), 2);
        probe.GatedPaths.Add(@"C:\v\b.mp4"); // b's scan stays open

        await vm.AddFilesAsync(new[] { @"C:\v\a.mp4", @"C:\v\b.mp4" });
        var rowA = vm.Items.Single(i => i.Path == @"C:\v\a.mp4");
        var rowB = vm.Items.Single(i => i.Path == @"C:\v\b.mp4");
        rowA.IntroEnd.Requested = TimeSpan.FromSeconds(10); // A ready + valid

        rowA.KeyframesReady.Should().BeTrue();
        rowB.KeyframesReady.Should().BeFalse();
        vm.CanRunBatch.Should().BeFalse("row B is enabled but still indexing keyframes");

        probe.ReleaseScans();
        await rowB.CurrentScanTask;

        vm.CanRunBatch.Should().BeTrue("A is valid and B (no-op at intro 0) is auto-disabled");
    }

    [Fact]
    public async Task CanRunBatch_False_WhenNoValidRow_TrueAfterValidCut()
    {
        var (vm, probe, _, _) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 0); // intro 0 → NoOpTrim

        vm.CanRunBatch.Should().BeFalse("the only row is a no-op trim");
        vm.RunLabel.Should().Be("Run bulk cut (0)");

        row.IntroEnd.Requested = TimeSpan.FromSeconds(10);

        vm.CanRunBatch.Should().BeTrue();
        vm.RunLabel.Should().Be("Run bulk cut (1)");
    }

    // ---- Delegation (the critic's decomposition fix) ----------------------------------------

    [Fact]
    public async Task RunBatch_Delegates_CallsBulkEngineOnce_WithOneItemPerEnabledValidRow_NeverCallsSplitEngine()
    {
        var (vm, probe, split, engine) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10); // valid
        await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 12); // valid
        await AddRowAsync(vm, probe, @"C:\v\c.mp4", 60, 2, introSeconds: 0);  // no-op → excluded

        vm.CanRunBatch.Should().BeTrue();
        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(1, "the whole batch is one RunAsync call");
        engine.ReceivedItems.Should().HaveCount(2, "one item per enabled+valid row (the no-op row is excluded)");
        engine.ReceivedItems!.Select(i => i.InputPath).Should().BeEquivalentTo(new[] { @"C:\v\a.mp4", @"C:\v\b.mp4" });
        engine.ReceivedItems.Should().OnlyContain(i => i.Tag is BulkItemViewModel);
        split.WasCalled.Should().BeFalse("RunBatchAsync must delegate — it must never call ISplitEngine.SplitAsync directly");
    }

    [Fact]
    public async Task RunBatch_PassesCollisionPolicy_ToOptions()
    {
        var (vm, probe, _, engine) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);
        vm.Overwrite = true;

        await vm.RunBatchAsync();

        engine.ReceivedOptions!.Collision.Should().Be(CollisionPolicy.Overwrite, "the per-run Overwrite toggle maps to CollisionPolicy.Overwrite");
    }

    // ---- Weighted / monotonic overall progress ----------------------------------------------

    [Fact]
    public void WeightedOverall_IsWeighted_And_Monotonic_ReachingOne()
    {
        var weights = new[] { 10d, 30d }; // the second row is 3× heavier

        // Monotonic non-decreasing across the natural (index, fraction) progression.
        var sequence = new (int Index, double Fraction)[]
        {
            (0, 0.0), (0, 0.5), (0, 1.0), (1, 0.0), (1, 0.5), (1, 1.0),
        };
        var previous = -1d;
        foreach (var (index, fraction) in sequence)
        {
            var value = BulkCutViewModel.WeightedOverall(weights, index, fraction);
            value.Should().BeGreaterThanOrEqualTo(previous);
            previous = value;
        }

        // Weighted: finishing the light row (10/40) is only 0.25; finishing both reaches exactly 1.0.
        BulkCutViewModel.WeightedOverall(weights, 1, 0.0).Should().BeApproximately(0.25, 1e-9);
        BulkCutViewModel.WeightedOverall(weights, 1, 1.0).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task RunBatch_AllValid_CompletesWithEveryRowDone_ReachingFullProgress()
    {
        var (vm, probe, _, engine) = Build();
        var r1 = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 30, 2, introSeconds: 4);
        var r2 = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 90, 2, introSeconds: 6);
        engine.ProgressScript = new[]
        {
            new BulkTrimProgress(0, 2, "a.mp4", 1.0, 0.25, BulkTrimPhase.Item),
            new BulkTrimProgress(1, 2, "b.mp4", 1.0, 1.0, BulkTrimPhase.Item),
        };

        await vm.RunBatchAsync();

        vm.BatchState.Should().Be(BulkBatchState.Completed);
        vm.Operation.State.Should().Be(OperationState.Completed);
        r1.RowState.Should().Be(RowState.Done);
        r2.RowState.Should().Be(RowState.Done);
        r1.Progress.Should().Be(1.0, "a Done row's per-row fraction reaches 1");
        r2.Progress.Should().Be(1.0);
    }

    // ---- Ledger fan-out by Tag --------------------------------------------------------------

    [Fact]
    public async Task RunBatch_Ledger_RoutesOutcomesByTag_SetsRowStates()
    {
        var (vm, probe, _, engine) = Build();
        var r1 = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);
        var r2 = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 12);
        var r3 = await AddRowAsync(vm, probe, @"C:\v\c.mp4", 60, 2, introSeconds: 14);

        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.CompletedWithFailures,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, @"C:\v\a_trimmed.mp4", null, new[] { "coarse note" }),
                new(items[1], ItemOutcome.Failed, null, new UserFacingError(ErrorCategory.Unknown, "boom", "tail"), Array.Empty<string>()),
                new(items[2], ItemOutcome.Skipped, null, null, Array.Empty<string>()),
            });

        await vm.RunBatchAsync();

        r1.RowState.Should().Be(RowState.Done);
        r1.OutputPath.Should().Be(@"C:\v\a_trimmed.mp4", "the collision-resolved written path comes back on the ledger");
        r1.Warning.Should().Contain("coarse note");

        r2.RowState.Should().Be(RowState.Failed);
        r2.Error.Should().NotBeNull();

        r3.RowState.Should().Be(RowState.Skipped);

        vm.BatchState.Should().Be(BulkBatchState.CompletedWithFailures);
        vm.Operation.State.Should().Be(OperationState.Completed, "CompletedWithFailures is NOT an op-level failure");
        vm.Operation.ResultSummary.Should().Be("Trimmed 1, 1 failed");
        vm.FailedCount.Should().Be(1);
        vm.LastFailedItems.Should().ContainSingle();
    }

    // ---- Blocked / Cancelled ----------------------------------------------------------------

    [Fact]
    public async Task RunBatch_Blocked_SetsAggregateFailed_DiskFull()
    {
        var (vm, probe, _, engine) = Build();
        var r1 = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);
        await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 12);

        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Blocked,
            items.Select(i => new BulkTrimItemResult(i, ItemOutcome.NotStarted, null, null, Array.Empty<string>())).ToList());

        await vm.RunBatchAsync();

        vm.Operation.State.Should().Be(OperationState.Failed);
        vm.Operation.Error!.Category.Should().Be(ErrorCategory.DiskFull);
        vm.BatchState.Should().Be(BulkBatchState.Blocked);
        r1.RowState.Should().Be(RowState.Ready, "a NotStarted row reverts to its computed state (re-runnable)");
        vm.Items.Should().OnlyContain(i => i.RowState != RowState.Done);
    }

    [Fact]
    public async Task RunBatch_Cancelled_SetsAggregateCancelled_RowsCancelledOrNotStarted()
    {
        var (vm, probe, _, engine) = Build();
        var r1 = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);
        var r2 = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 60, 2, introSeconds: 12);
        var r3 = await AddRowAsync(vm, probe, @"C:\v\c.mp4", 60, 2, introSeconds: 14);

        engine.BeforeReturn = () => vm.Operation.CancelCommand.Execute(null); // cancel mid-run
        engine.ResultFactory = (items, ct) =>
        {
            ct.IsCancellationRequested.Should().BeTrue("the engine observes the aggregate op's token");
            return new BatchResult(
                BatchOutcome.Cancelled,
                new List<BulkTrimItemResult>
                {
                    new(items[0], ItemOutcome.Done, @"C:\v\a_trimmed.mp4", null, Array.Empty<string>()),
                    new(items[1], ItemOutcome.Cancelled, null, null, Array.Empty<string>()),
                    new(items[2], ItemOutcome.NotStarted, null, null, Array.Empty<string>()),
                });
        };

        await vm.RunBatchAsync();

        vm.Operation.State.Should().Be(OperationState.Cancelled);
        vm.BatchState.Should().Be(BulkBatchState.Cancelled);
        r1.RowState.Should().Be(RowState.Done, "done rows are kept");
        r2.RowState.Should().Be(RowState.Cancelled, "the in-flight row is cancelled");
        r3.RowState.Should().Be(RowState.Ready, "a not-started row reverts to its computed state");
    }

    // ---- Dedup ------------------------------------------------------------------------------

    [Fact]
    public async Task AddFiles_DedupsByFullPath()
    {
        var (vm, probe, _, _) = Build();
        probe.SetUniform(@"C:\v\ep.mp4", TimeSpan.FromSeconds(60), 2);

        await vm.AddFilesAsync(new[] { @"C:\v\ep.mp4", @"C:\v\ep.mp4" }); // dup within one call
        await vm.AddFilesAsync(new[] { @"C:\v\ep.mp4" });                  // dup across calls

        vm.Items.Should().ContainSingle("never a second row per source (dedup by GetFullPath)");
    }

    // ---- Bounded keyframe-scan throttle -----------------------------------------------------

    [Fact]
    public async Task BoundedScan_MaxThreeConcurrentGetKeyframes()
    {
        var (vm, probe, _, _) = Build();
        probe.GateEverything = true; // hold every scan open so concurrency is observable

        var paths = Enumerable.Range(0, 6).Select(i => $@"C:\v\ep{i}.mp4").ToArray();
        foreach (var p in paths)
        {
            probe.SetUniform(p, TimeSpan.FromSeconds(60), 2);
        }

        await vm.AddFilesAsync(paths);

        probe.PeakScans.Should().Be(3, "the shared SemaphoreSlim(3) bounds concurrent ffprobe scans to 3");

        probe.ReleaseScans();
        await Task.WhenAll(vm.Items.Select(i => i.CurrentScanTask));

        probe.PeakScans.Should().Be(3, "the bound is never exceeded as the remaining scans drain");
        vm.Items.Should().OnlyContain(i => i.KeyframesReady);
    }

    // ---- Clear ------------------------------------------------------------------------------

    [Fact]
    public async Task Clear_DropsRows_AndResetsState()
    {
        var (vm, probe, _, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 60, 2, introSeconds: 10);
        vm.CanClear.Should().BeTrue();

        vm.Clear();

        vm.Items.Should().BeEmpty();
        vm.BatchState.Should().Be(BulkBatchState.Idle);
        vm.CanRunBatch.Should().BeFalse();
    }

    // ==== SPEC-011 gaps (todo-automate) ======================================================
    //
    // Both progress gaps below run under an INLINE synchronization context. The batch fans progress
    // through two Progress<T> channels (engine sample → OnBatchProgress → the overall bar), and each
    // marshals onto the context captured when it was constructed; running those posts inline is what
    // makes the fan-out ORDERED and observable MID-RUN — i.e. before the ledger folds terminal row
    // states on. Under the default thread-pool posting the samples land unordered and after the run.

    /// <summary>Runs every posted callback inline, in order (see the note above).</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    // SPEC-011#I42 — the reported overall bar is monotonic-clamped: OnBatchProgress only ever reports
    // a value ≥ the last one it reported (_progressLock / _lastOverall), so a late/out-of-order engine
    // sample whose raw weighted overall is LOWER can never pull the bar backwards.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task OverallProgress_NeverGoesBackwards_WhenTheEngineReportsOutOfOrder()
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            var (vm, probe, _, engine) = Build();
            await AddRowAsync(vm, probe, @"C:\v\a.mp4", 30, 2, introSeconds: 4);
            await AddRowAsync(vm, probe, @"C:\v\b.mp4", 90, 2, introSeconds: 6);

            var seen = new List<double>();
            vm.Operation.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OperationViewModel.Progress))
                {
                    seen.Add(vm.Operation.Progress);
                }
            };

            // Deliberately out of order: the batch races ahead to "row 1 finished" (overall 1.0), then
            // two STALE samples arrive whose raw weighted overall is far lower.
            engine.ProgressScript = new[]
            {
                new BulkTrimProgress(0, 2, "a.mp4", 0.5, 0.10, BulkTrimPhase.Item),
                new BulkTrimProgress(1, 2, "b.mp4", 1.0, 1.00, BulkTrimPhase.Item),
                new BulkTrimProgress(0, 2, "a.mp4", 0.25, 0.05, BulkTrimPhase.Item), // stale + lower
                new BulkTrimProgress(1, 2, "b.mp4", 0.1, 0.20, BulkTrimPhase.Item),  // stale + lower
            };

            await vm.RunBatchAsync();

            seen.Should().NotBeEmpty("the overall bar was reported while the batch ran");
            seen.Should().BeInAscendingOrder(
                "every report is clamped to the running maximum (_lastOverall) — the bar never rewinds");
            seen[^1].Should().Be(1d, "the highest overall reached (row 1 finished) is what stays on the bar");
            vm.Operation.Progress.Should().Be(1d, "the two later, lower samples never pulled the bar back");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    // SPEC-011#I43 — per-row progress fans out by ItemIndex: ONLY the addressed row is advanced to
    // Running and given that sample's ItemFraction. A row no Item sample addresses (and a batch-level,
    // non-Item sample naming it) leaves the row exactly as MarkQueued left it.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task PerRowProgress_FansOutByItemIndex_AdvancingOnlyTheAddressedRow()
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            var (vm, probe, _, engine) = Build();
            var r1 = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 30, 2, introSeconds: 4);
            var r2 = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 90, 2, introSeconds: 6);

            engine.ProgressScript = new[]
            {
                new BulkTrimProgress(0, 2, "a.mp4", 0.25, 0.1, BulkTrimPhase.Item),

                // A batch-level (non-Item) sample naming row 1 must NOT advance that row.
                new BulkTrimProgress(1, 2, string.Empty, 0.9, 0.5, BulkTrimPhase.Running),
            };

            // Observe the rows MID-RUN: BeforeReturn fires after the samples replay but before the
            // ledger folds terminal states (Done/Failed/…) onto every row.
            RowState addressedState = default, otherState = default;
            double addressedProgress = -1d, otherProgress = -1d;
            engine.BeforeReturn = () =>
            {
                addressedState = r1.RowState;
                addressedProgress = r1.Progress;
                otherState = r2.RowState;
                otherProgress = r2.Progress;
            };

            await vm.RunBatchAsync();

            addressedState.Should().Be(RowState.Running, "the addressed row (ItemIndex 0) is advanced to Running");
            addressedProgress.Should().Be(0.25, "…and takes that sample's ItemFraction verbatim");
            otherState.Should().Be(RowState.Queued, "a row no Item sample addressed stays as MarkQueued left it");
            otherProgress.Should().Be(0d, "…with its per-row fraction untouched");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }
}
