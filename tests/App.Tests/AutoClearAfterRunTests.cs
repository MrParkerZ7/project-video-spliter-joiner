using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Io;
using Xunit;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-171 / G-055 — "auto clear all after done", a checkbox beside Replace originals.
///
/// <para><b>Why this is not "call Clear() when the batch ends".</b> <c>Clear()</c> also resets the operation,
/// the batch state and the failed-rows list, so wiring it to the end of a run erases the run's report at the
/// moment it is produced — the user would watch a batch finish and be left with an empty screen and no account
/// of what happened. This takes the FINISHED rows out and keeps the report.</para>
///
/// <para><b>What it takes and what it says</b> (G-055 criterion 3). T-171 kept a finished row whose original was still
/// on disk (decision (b)) and a row the run warned on — which, in the default new-file mode, kept every row, and the user
/// reported the list as still there (T-183). Since T-185 every row the run finished clears; the summary says the
/// originals are still on disk and names any row that was not cut exactly. What stays: a row not in the run (unticked,
/// no cut yet, added while the batch ran), and — only with Auto-delete armed — a row whose original the sweep could not
/// bin, unticked so the delete can be retried without the next Run trimming it again.</para>
/// </summary>
public sealed class AutoClearAfterRunTests
{
    // Built from the engine's own constant (T-185), so a reworded engine warning cannot pass here on a stale copy.
    private const string ExactFellBack =
        BulkTrimEngine.ExactFallbackPrefix + " (no encoder for this codec) - cut snapped to the nearest keyframe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t171-" + Guid.NewGuid().ToString("N"));

    /// <summary>Records what it was handed and removes the file — the real disposer's best-effort contract.</summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        public Func<string, bool> RefuseWhen { get; init; } = _ => false;

        public void DisposeOriginalBackup(string backupPath)
        {
            Disposed.Add(backupPath);
            if (!RefuseWhen(backupPath))
            {
                try { File.Delete(backupPath); } catch { /* mirrors the real disposer */ }
            }
        }
    }

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "video bytes");
        return p;
    }

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeBulkTrimEngine Engine, FakeSettings Settings) Build(
        IOriginalDisposer? disposer)
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, engine,
            originalDisposer: disposer);
        vm.LookupFileHolders = _ => Array.Empty<string>();
        return (vm, probe, engine, settings);
    }

    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double introSeconds = 10)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 2);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        return row;
    }

    private static void ReplaceMode(BulkCutViewModel vm)
    {
        vm.ReplaceOriginal = true;
        vm.ConfirmReplaceOriginals = _ => true;
    }

    /// <summary>A clean batch that wrote a NEW file beside each original — the originals stay on disk (T-185: and the
    /// rows still clear; T-171 kept them).</summary>
    private static void NewFileOutputs(
        FakeBulkTrimEngine engine, string[] outputs, Func<int, IReadOnlyList<string>>? warnings = null)
        => engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            items.Select((item, i) => new BulkTrimItemResult(
                    item, ItemOutcome.Done, outputs[i], null, warnings?.Invoke(i) ?? Array.Empty<string>()))
                .ToList());

    private static void NewFileOutputs(FakeBulkTrimEngine engine, params string[] outputs)
        => NewFileOutputs(engine, outputs, warnings: null);

    /// <summary>A clean batch under Replace originals — each output IS its original.</summary>
    private static void ReplacedInPlace(FakeBulkTrimEngine engine, Func<int, IReadOnlyList<string>>? warnings = null)
        => engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            items.Select((item, i) => new BulkTrimItemResult(
                    item, ItemOutcome.Done, item.InputPath, null, warnings?.Invoke(i) ?? Array.Empty<string>()))
                .ToList());

    // ---- The feature ---------------------------------------------------------------------------------

    /// <summary>
    /// Under Replace originals there is never an original left to delete, so the list clears — with auto-delete OFF.
    /// This is the case that rules out option (c): gating auto-clear on auto-delete would switch it off in exactly
    /// the mode the user asked for it beside. A full clear also drops the selection and the list's notes, exactly as
    /// Clear all does.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task UnderReplaceOriginals_ACleanBatchEmptiesTheList_WithAutoDeleteOff()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await vm.AddDroppedFilesAsync(new[] { Path.Combine(_dir, "notes.txt") });
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("b.mp4"));
        ReplaceMode(vm);
        ReplacedInPlace(engine);
        vm.AutoDeleteOriginals.Should().BeFalse("precondition: auto-delete is off");
        vm.SelectedItem.Should().NotBeNull("precondition: the first row was auto-selected");
        vm.DropSummary.Should().NotBeNull("precondition: the drop note is showing");
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEmpty("a clean batch with the box ticked leaves the list empty");
        vm.SelectedItem.Should().BeNull("nothing is left to preview");
        vm.DropSummary.Should().BeNull("a note about the list would sit over an empty list, contradicting it");
    }

    /// <summary>
    /// ❗ The report survives the clear. Reusing <c>Clear()</c> verbatim would reset the operation and blank the
    /// summary — the mutation this test exists to kill. The report's Open folder button keeps a target, although the
    /// rows it used to look in are gone.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheRunsReportIsStillOnScreen_AndOpenFolderStillHasATarget_AfterTheListClears()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        var first = await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("b.mp4"));
        ReplaceMode(vm);
        ReplacedInPlace(engine);
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEmpty("precondition: the list was cleared");
        vm.Operation.IsCompleted.Should().BeTrue("the completed surface stays up — it is the report");
        vm.Operation.ResultSummary.Should().Be("Trimmed 2", "the account of what the run did outlives its rows");
        vm.BatchState.Should().Be(BulkBatchState.Completed);
        vm.LastRunOutputPath.Should().Be(first.Path, "Open folder reveals the run's first output even with no rows left");
    }

    /// <summary>
    /// With auto-delete also armed, the originals are deleted and THEN the list clears — clearing first would leave the
    /// delete sweep no rows. And the sweep's line joins the run's line instead of replacing it, or "Trimmed N" would
    /// survive nowhere once the rows are gone.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WithAutoDelete_TheOriginalsAreBinnedFirst_ThenTheListClears_AndBothLinesSurvive()
    {
        var disposer = new RecordingDisposer();
        var (vm, probe, engine, _) = Build(disposer);
        var a = await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        var b = await AddRowAsync(vm, probe, MakeVideo("b.mp4"));
        NewFileOutputs(engine, MakeVideo("a_trimmed.mp4"), MakeVideo("b_trimmed.mp4"));
        vm.AutoDeleteOriginals = true;
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        disposer.Disposed.Should().BeEquivalentTo(new[] { a.Path, b.Path }, "auto-delete ran on the rows before they went");
        vm.Items.Should().BeEmpty("every original was binned, so nothing is left for the user to do by hand");
        vm.Operation.ResultSummary.Should().StartWith("Trimmed 2", "what the run cut is still said")
            .And.Contain("Recycle Bin", "and so is what the sweep binned");
    }

    // ---- What is never taken away ----------------------------------------------------------------------

    /// <summary>
    /// T-185 (T-183, the user's own case) — spec change: auto-delete off, new files written, so every original is still
    /// on disk. T-171 decision (b) kept every row here (this test was <c>OriginalsLeftToDelete_KeepTheirRows_…</c>), so
    /// in the default mode Auto-clear cleared nothing. The user kept their originals by leaving auto-delete off; the rows
    /// now clear and the summary says the originals are still on disk.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OriginalsKeptOnDisk_TheRowsStillClear_AndTheSummarySaysTheOriginalsAreOnDisk()
    {
        var disposer = new RecordingDisposer();
        var (vm, probe, engine, _) = Build(disposer);
        var originals = new[] { MakeVideo("a.mp4"), MakeVideo("b.mp4"), MakeVideo("c.mp4") };
        foreach (var original in originals)
        {
            await AddRowAsync(vm, probe, original);
        }

        var before = originals.Select(File.ReadAllBytes).ToList();
        NewFileOutputs(engine, MakeVideo("a_trimmed.mp4"), MakeVideo("b_trimmed.mp4"), MakeVideo("c_trimmed.mp4"));
        vm.AutoDeleteOriginals.Should().BeFalse("precondition: the user's setting");
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEmpty("the user asked for the finished list to go, and kept their originals on purpose");
        vm.Operation.ResultSummary.Should().Be("Trimmed 3 · Cleared 3 from the list — their originals are still on disk");
        originals.Select(File.ReadAllBytes).Should().BeEquivalentTo(before, o => o.WithStrictOrdering(),
            "the originals are untouched");
        disposer.Disposed.Should().BeEmpty("nothing was sent anywhere");
        vm.CanDeleteOriginals.Should().BeFalse("no row is left for ✕ Delete originals to act on");
    }

    /// <summary>T-185 — one original reads in the singular.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OneOriginalKept_ReadsInTheSingular()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        NewFileOutputs(engine, MakeVideo("a_trimmed.mp4"));
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Operation.ResultSummary.Should().Be("Trimmed 1 · Cleared 1 from the list — its original is still on disk");
    }

    /// <summary>
    /// Auto-delete armed, one original binned and one refused (still in use). The binned row goes; the refused row's
    /// original is still on disk for the user to deal with, so that row — and only that row — stays. T-185 — spec change:
    /// the clause reads "still in use" (was "1 original left to delete"), and the kept row is unticked so the next Run
    /// does not trim it again.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARefusedOriginal_KeepsItsRow_WhileTheBinnedOneGoes()
    {
        var held = MakeVideo("held.mp4");
        var (vm, probe, engine, _) = Build(new RecordingDisposer { RefuseWhen = p => p == held });
        var heldRow = await AddRowAsync(vm, probe, held);
        await AddRowAsync(vm, probe, MakeVideo("free.mp4"));
        NewFileOutputs(engine, MakeVideo("held_trimmed.mp4"), MakeVideo("free_trimmed.mp4"));
        vm.AutoDeleteOriginals = true;
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().Equal(new[] { heldRow }, "only the row whose original is still there is kept");
        vm.Operation.ResultSummary.Should().Contain("Still in use", "the delete sweep's report is kept")
            .And.Contain("Kept in the list: 1 original still in use", "and the reason the row stayed is added to it");
        vm.Operation.ResultSummary.Should().NotContain("Cleared", "the binned original is not on disk to mention");
        heldRow.IsCheckedByUser.Should().BeFalse("a kept, finished row must not be trimmed again by the next Run");
    }

    /// <summary>
    /// T-185 — the kept row is how the user retries the delete, and doing so must not erase the run's report: a manual
    /// ✕ Delete originals joins its line to the report while a completed run's report is on screen, exactly as the
    /// automatic sweep's does (it used to replace the whole summary).
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RetryingTheDeleteOnTheKeptRow_KeepsTheRunsReport()
    {
        var held = MakeVideo("held.mp4");
        var locked = true;
        var disposer = new RecordingDisposer { RefuseWhen = p => locked && p == held };
        var (vm, probe, engine, _) = Build(disposer);
        var heldRow = await AddRowAsync(vm, probe, held);
        await AddRowAsync(vm, probe, MakeVideo("free.mp4"));
        NewFileOutputs(engine, MakeVideo("held_trimmed.mp4"), MakeVideo("free_trimmed.mp4"));
        vm.AutoDeleteOriginals = true;
        vm.AutoClearAfterRun = true;
        await vm.RunBatchAsync();
        vm.Items.Should().Equal(new[] { heldRow }, "precondition: the refused row stayed");

        locked = false;
        vm.ConfirmDeleteOriginals = (_, _) => true;
        vm.DeleteOriginals();

        File.Exists(held).Should().BeFalse("the retry binned it");
        vm.Operation.ResultSummary.Should().StartWith("Trimmed 2", "what the run trimmed is still said")
            .And.EndWith("Sent 1 original(s) to the Recycle Bin", "and the retry's line is added, not substituted");
        vm.Operation.ResultSummary.Should().NotContain("Kept in the list",
            "the stale 'still in use' clause goes once the user has acted on it");
    }

    /// <summary>
    /// T-185 — without Auto-clear, the automatic sweep's line is still part of the run's report: a manual retry keeps it
    /// and adds its own after it.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WithoutAutoClear_AManualRetryKeepsTheAutomaticSweepsLine()
    {
        var held = MakeVideo("held.mp4");
        var locked = true;
        var (vm, probe, engine, _) = Build(new RecordingDisposer { RefuseWhen = p => locked && p == held });
        await AddRowAsync(vm, probe, held);
        await AddRowAsync(vm, probe, MakeVideo("free.mp4"));
        NewFileOutputs(engine, MakeVideo("held_trimmed.mp4"), MakeVideo("free_trimmed.mp4"));
        vm.AutoDeleteOriginals = true;
        await vm.RunBatchAsync();
        var afterRun = vm.Operation.ResultSummary!;
        afterRun.Should().StartWith("Trimmed 2 · Sent 1 to the Recycle Bin. Still in use: held.mp4", "precondition");

        locked = false;
        vm.ConfirmDeleteOriginals = (_, _) => true;
        vm.DeleteOriginals();

        vm.Operation.ResultSummary.Should().Be(afterRun + " · Sent 1 original(s) to the Recycle Bin");
    }

    /// <summary>
    /// T-185 review — if some cleared rows' originals are no longer on disk (moved or binned by hand during the run), the
    /// clause gives both counts rather than a wrong number of cleared rows.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WhenOnlySomeOriginalsAreStillOnDisk_TheClauseGivesBothCounts()
    {
        var gone = MakeVideo("gone.mp4");
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, gone);
        await AddRowAsync(vm, probe, MakeVideo("b.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("c.mp4"));
        NewFileOutputs(engine, MakeVideo("gone_trimmed.mp4"), MakeVideo("b_trimmed.mp4"), MakeVideo("c_trimmed.mp4"));
        engine.BeforeReturn = () => File.Delete(gone);   // the user bins one original by hand while the batch runs
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEmpty();
        vm.Operation.ResultSummary.Should().Be("Trimmed 3 · Cleared 3 from the list — 2 originals still on disk");
    }

    /// <summary>
    /// T-185 review — pressing ✕ Delete originals again while the file is still held reports the latest attempt; it does
    /// not append another copy each time.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RetryingTwiceWhileStillHeld_ReportsTheLatestAttempt_WithoutGrowing()
    {
        var held = MakeVideo("held.mp4");
        var (vm, probe, engine, _) = Build(new RecordingDisposer { RefuseWhen = p => p == held });
        await AddRowAsync(vm, probe, held);
        await AddRowAsync(vm, probe, MakeVideo("free.mp4"));
        NewFileOutputs(engine, MakeVideo("held_trimmed.mp4"), MakeVideo("free_trimmed.mp4"));
        vm.AutoDeleteOriginals = true;
        vm.AutoClearAfterRun = true;
        await vm.RunBatchAsync();
        vm.ConfirmDeleteOriginals = (_, _) => true;

        vm.DeleteOriginals();
        var once = vm.Operation.ResultSummary;
        vm.DeleteOriginals();

        vm.Operation.ResultSummary.Should().Be(once, "a second press replaces the first press's line, it does not add to it");
        once.Should().StartWith("Trimmed 2 · Sent 1 to the Recycle Bin").And.EndWith("Still in use: held.mp4");
    }

    /// <summary>
    /// T-185 review — the join is onto the RUN's report only. After another gesture replaced the summary (a profile
    /// backup), a manual delete replaces it as before instead of welding two unrelated messages together.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AManualDeleteAfterAnotherGesturesMessage_ReplacesIt()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        NewFileOutputs(engine, MakeVideo("a_trimmed.mp4"));
        await vm.RunBatchAsync();   // auto-clear off: the row stays, its original deletable
        var backup = Path.Combine(_dir, "profiles.vsjprofiles");
        vm.ChooseProfileExportPath = () => backup;
        vm.ExportProfiles();
        vm.Operation.ResultSummary.Should().StartWith("Exported", "precondition: another gesture's message is up");

        vm.ConfirmDeleteOriginals = (_, _) => true;
        vm.DeleteOriginals();

        vm.Operation.ResultSummary.Should().Be("Sent 1 original(s) to the Recycle Bin");
    }

    /// <summary>T-185 — with no clean run's report standing (a failed batch), a manual delete replaces the summary as before.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AManualDeleteAfterAPartlyFailedBatch_ReplacesTheSummary()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("good.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("bad.mp4"));
        var output = MakeVideo("good_trimmed.mp4");
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.CompletedWithFailures,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, output, null, Array.Empty<string>()),
                new(items[1], ItemOutcome.Failed, null,
                    new UserFacingError(ErrorCategory.Unknown, "boom", "tail"), Array.Empty<string>()),
            });
        await vm.RunBatchAsync();

        vm.ConfirmDeleteOriginals = (_, _) => true;
        vm.DeleteOriginals();

        vm.Operation.ResultSummary.Should().Be("Sent 1 original(s) to the Recycle Bin");
    }

    /// <summary>
    /// Rows that were not part of the run — unticked, or with no cut set yet — are not "the finished list". Taking them
    /// away would silently discard work the user set aside for later, so they stay and the summary counts them.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RowsThatWereNotInTheRun_StayInTheList_AndTheSummaryCountsThem()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("ran.mp4"));
        var unticked = await AddRowAsync(vm, probe, MakeVideo("later.mp4"));
        unticked.IsCheckedByUser = false;
        var noCut = await AddRowAsync(vm, probe, MakeVideo("nocut.mp4"), introSeconds: 0);
        noCut.IsEnabled.Should().BeFalse("precondition: a row with no cut is not in the run");
        ReplaceMode(vm);
        ReplacedInPlace(engine);
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEquivalentTo(new[] { unticked, noCut }, "only the row the run finished goes");
        vm.Operation.ResultSummary.Should().Be("Trimmed 1 · Kept in the list: 2 not in this run");
        noCut.IsCheckedByUser.Should().BeTrue("T-185: a row not in the run keeps its tick - only a kept, finished row is unticked");
        unticked.IsCheckedByUser.Should().BeFalse("and an unticked one stays unticked");
        vm.SelectedItem.Should().BeNull("the previewed row was the one taken out, so nothing it pointed at remains");
    }

    /// <summary>
    /// Files dropped while a batch runs — G-055's own workflow, "loading the next season" — are not part of that run.
    /// They must survive its clear.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowAddedWhileTheBatchRan_StaysInTheList()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("season1.mp4"));
        var next = MakeVideo("season2.mp4");
        probe.SetUniform(next, TimeSpan.FromSeconds(60), 2);
        engine.BeforeReturn = () => _ = vm.AddFilesAsync(new[] { next });
        ReplaceMode(vm);
        ReplacedInPlace(engine);
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Select(i => i.Path).Should().Equal(new[] { next }, "the file added mid-run was never in the run");
        vm.Items.Single().IsCheckedByUser.Should().BeTrue("it keeps its tick for the next batch (T-185)");
        vm.Operation.ResultSummary.Should().Contain("Kept in the list: 1 not in this run");
    }

    /// <summary>
    /// A row the run reported a warning on — here an exact cut that fell back to a keyframe and was written over the
    /// original. T-185 — spec change (was <c>ARowTheRunWarnedAbout_StaysInTheList_…</c>): keeping warned rows kept the
    /// list full for the same user, since every row of a codec that cannot be re-encoded falls back. The row clears and
    /// the summary names it — the fallback is the one warning the user could not have seen before Run.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowTheRunWarnedAbout_Clears_AndItsExactCutFallbackIsNamedInTheSummary()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("warned.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("clean.mp4"));
        ReplaceMode(vm);
        ReplacedInPlace(engine, i => i == 0 ? new[] { ExactFellBack } : Array.Empty<string>());
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.BatchState.Should().Be(BulkBatchState.Completed, "precondition: a warning does not make the batch unclean");
        vm.Items.Should().BeEmpty("a warned row clears like the rest");
        vm.Operation.ResultSummary.Should().Be("Trimmed 2 · Not cut exactly (snapped to a keyframe): warned.mp4");
    }

    /// <summary>
    /// T-185 (the warned-rows repro) — in the user's own mode, a planner note (an outro-only row's ignored cut at 0:00)
    /// and an exact-cut fallback both used to keep their rows. Both clear; only the fallback is named — the planner's
    /// notes were on the row before Run.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task PlannerNotesAndFallbacks_DoNotHoldTheList_OnlyTheFallbackIsNamed()
    {
        const string PlannerNote = "Cut at 0:00 is outside the file bounds [0:00, 1:00] and was ignored.";
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("outro-only.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("fell-back.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("clean.mp4"));
        NewFileOutputs(
            engine,
            new[] { MakeVideo("outro-only_trimmed.mp4"), MakeVideo("fell-back_trimmed.mp4"), MakeVideo("clean_trimmed.mp4") },
            i => i switch { 0 => new[] { PlannerNote }, 1 => new[] { ExactFellBack }, _ => Array.Empty<string>() });
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEmpty();
        vm.Operation.ResultSummary.Should().Be(
            "Trimmed 3 · Cleared 3 from the list — their originals are still on disk"
            + " · Not cut exactly (snapped to a keyframe): fell-back.mp4");
    }

    /// <summary>T-185 — exactly three names needs no "and N more".</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ExactlyThreeFallbacks_AreAllNamed()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        for (var i = 1; i <= 3; i++)
        {
            await AddRowAsync(vm, probe, MakeVideo($"ep0{i}.mp4"));
        }

        ReplaceMode(vm);
        ReplacedInPlace(engine, _ => new[] { ExactFellBack });
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Operation.ResultSummary.Should().EndWith("Not cut exactly (snapped to a keyframe): ep01.mp4, ep02.mp4, ep03.mp4");
    }

    /// <summary>T-185 — both "Kept in the list" parts, in order, comma-separated, plural.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BothKeptParts_ReadTogether_InThePlural()
    {
        var a = MakeVideo("held-a.mp4");
        var b = MakeVideo("held-b.mp4");
        var (vm, probe, engine, _) = Build(new RecordingDisposer { RefuseWhen = p => p == a || p == b });
        await AddRowAsync(vm, probe, a);
        await AddRowAsync(vm, probe, b);
        var later = await AddRowAsync(vm, probe, MakeVideo("later.mp4"));
        later.IsCheckedByUser = false;
        NewFileOutputs(engine, MakeVideo("held-a_trimmed.mp4"), MakeVideo("held-b_trimmed.mp4"));
        vm.AutoDeleteOriginals = true;
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Operation.ResultSummary.Should().EndWith("Kept in the list: 2 originals still in use, 1 not in this run");
    }

    /// <summary>T-185 — at most three names, then how many more.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ManyFallbacks_NameThree_ThenCountTheRest()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        for (var i = 1; i <= 5; i++)
        {
            await AddRowAsync(vm, probe, MakeVideo($"ep0{i}.mp4"));
        }

        ReplaceMode(vm);
        ReplacedInPlace(engine, _ => new[] { ExactFellBack });
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Operation.ResultSummary.Should().EndWith(
            "Not cut exactly (snapped to a keyframe): ep01.mp4, ep02.mp4, ep03.mp4 and 2 more");
    }

    // ---- Only a clean batch ----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task APartlyFailedBatch_DoesNotClear_AndTheFailedRowsAreStillListed()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("good.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("bad.mp4"));
        ReplaceMode(vm);
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.CompletedWithFailures,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, items[0].InputPath, null, Array.Empty<string>()),
                new(items[1], ItemOutcome.Failed, null,
                    new UserFacingError(ErrorCategory.Unknown, "boom", "tail"), Array.Empty<string>()),
            });
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().HaveCount(2, "a partly-failed run is exactly when the user needs to see the rows");
        vm.LastFailedItems.Should().ContainSingle("the only record of which rows failed survives");
    }

    /// <summary>Every outcome but a clean completion keeps the list — a gate that only lists two bad states would pass
    /// the test above and still wipe the list after, say, a disk-full block.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(BatchOutcome.Cancelled, ItemOutcome.Cancelled)]
    [InlineData(BatchOutcome.Blocked, ItemOutcome.NotStarted)]
    [InlineData(BatchOutcome.CompletedWithFailures, ItemOutcome.Skipped)]
    public async Task AnyOutcomeButACleanCompletion_KeepsTheList(BatchOutcome outcome, ItemOutcome item)
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        ReplaceMode(vm);
        engine.ResultFactory = (items, _) => new BatchResult(
            outcome,
            new List<BulkTrimItemResult> { new(items[0], item, null, null, Array.Empty<string>()) });
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().ContainSingle($"a {outcome} run did not finish cleanly, so nothing is tidied away");
        (vm.Operation.ResultSummary ?? string.Empty).Should().NotContain(
            "Kept in the list", "the auto-clear did not run at all, so it has nothing to explain");
    }

    /// <summary>An engine that throws leaves no result at all; the auto-clear must stay out of it entirely.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARunThatThrows_KeepsTheList_AndAddsNothingToTheReport()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        ReplaceMode(vm);
        engine.ResultFactory = (_, _) => throw new IOException("the engine fell over");
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Operation.Error.Should().NotBeNull("precondition: the run failed");
        vm.Items.Should().ContainSingle("a run that produced no result tidies nothing away");
        (vm.Operation.ResultSummary ?? string.Empty).Should().NotContain("Kept in the list");
    }

    // ---- Clear all is unchanged ------------------------------------------------------------------------

    /// <summary>
    /// The refactor split the row-dropping half out of <c>Clear()</c>. Pressing Clear all must still reset the whole
    /// report — the ticket rules out any change to what it does.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ClearAll_StillResetsTheWholeReport()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("good.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("bad.mp4"));
        ReplaceMode(vm);
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.CompletedWithFailures,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, items[0].InputPath, null, Array.Empty<string>()),
                new(items[1], ItemOutcome.Failed, null,
                    new UserFacingError(ErrorCategory.Unknown, "boom", "tail"), Array.Empty<string>()),
            });
        await vm.RunBatchAsync();
        vm.Operation.ResultSummary.Should().NotBeNull("precondition: there is a report to reset");

        vm.Clear();

        vm.Items.Should().BeEmpty();
        vm.Operation.ResultSummary.Should().BeNull();
        vm.Operation.IsCompleted.Should().BeFalse();
        vm.BatchState.Should().Be(BulkBatchState.Idle);
        vm.LastFailedItems.Should().BeEmpty();
        vm.LastRunOutputPath.Should().BeNull();
    }

    // ---- Default, persistence, and the shipped path ----------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OffByDefault_AndOffMeansTheListStays()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        ReplaceMode(vm);
        ReplacedInPlace(engine);

        vm.AutoClearAfterRun.Should().BeFalse("a first run must not make the list vanish before the option is known");
        await vm.RunBatchAsync();

        vm.Items.Should().ContainSingle();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheChoicePersists()
    {
        var (vm, _, _, settings) = Build(null);

        vm.AutoClearAfterRun = true;

        settings.BulkAutoClearAfterRun.Should().BeTrue("it is a preference, not a per-session toggle");
    }

    /// <summary>
    /// The shipped path: Run is a button bound to <c>RunBatchCommand</c>, which fires the batch without awaiting it.
    /// The clear must be reached through that command, not only through a test calling the method directly — the
    /// T-162 shape, where a feature is complete in tests and inert in the app.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheRunCommand_ReachesTheClear()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        ReplaceMode(vm);
        ReplacedInPlace(engine);
        vm.AutoClearAfterRun = true;

        vm.RunBatchCommand.CanExecute(null).Should().BeTrue("precondition: the button is enabled");
        vm.RunBatchCommand.Execute(null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (vm.Items.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        vm.Items.Should().BeEmpty("the button's own command path clears the list");
        vm.Operation.IsCompleted.Should().BeTrue();
        engine.CallCount.Should().Be(1, "the batch really ran through the command");
    }

    /// <summary>
    /// The checkbox exists in the shipped view, is bound two-way to the preference, and sits directly before the
    /// irreversible group — beside Replace originals, as asked — but outside it and without its red vocabulary: it
    /// discards screen state, never a file (SPEC-011 I146).
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheCheckboxIsWiredInTheView_RightBeforeReplaceOriginals_ButNotInTheDestructiveGroup()
    {
        var failures = new List<string>();

        StaViewHarness.OnSta(() =>
        {
            var vm = new BulkCutViewModel(
                new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                new FakeSettings(), new FakeBulkTrimEngine());
            var view = new BulkCutView { DataContext = vm };
            StaViewHarness.LayOut(view, 1280, 800);

            var options = StaViewHarness.Find<WrapPanel>(view, "FooterOptions");
            if (options is null)
            {
                failures.Add("FooterOptions not found");
                return;
            }

            var children = options.Children.Cast<object>().ToList();
            int IndexOfBoundCheckBox(string path) => children.FindIndex(c =>
                c is CheckBox cb && BindingOperations.GetBinding(cb, ToggleButton_IsChecked)?.Path?.Path == path);

            var clear = IndexOfBoundCheckBox(nameof(BulkCutViewModel.AutoClearAfterRun));
            var replace = IndexOfBoundCheckBox(nameof(BulkCutViewModel.ReplaceOriginal));
            if (clear < 0 || replace < 0)
            {
                failures.Add($"checkboxes not found (auto-clear {clear}, replace {replace})");
                return;
            }

            if (replace != clear + 2 || children[clear + 1] is not Rectangle)
            {
                failures.Add($"expected auto-clear, then the group separator, then Replace originals; got indexes {clear} and {replace}");
            }

            var box = (CheckBox)children[clear];
            if (box.Content is not string)
            {
                failures.Add("the label is not plain text - it copies the destructive group's glyph-and-colour markup");
            }

            var danger = (Brush)view.FindResource("DangerBrush");
            if (Equals(box.Foreground, danger) || StaViewHarness.Descendants<TextBlock>(box).Any(t => Equals(t.Foreground, danger)))
            {
                failures.Add("the checkbox wears the danger colour of the irreversible group");
            }

            box.IsChecked = true;
            if (!vm.AutoClearAfterRun)
            {
                failures.Add("ticking the box did not reach the view-model — the binding is not two-way");
            }

            if ((box.ToolTip as string ?? string.Empty).IndexOf("summary", StringComparison.OrdinalIgnoreCase) < 0)
            {
                failures.Add("the tooltip does not say the run's summary is kept");
            }
        });

        failures.Should().BeEmpty();
    }

    /// <summary>
    /// The report line now carries the run's line, the Recycle Bin line and why rows were kept. It has to WRAP inside
    /// the completed surface, with Open folder still inside it, at the narrowest window the app is tested at — a
    /// horizontal StackPanel measured it at infinite width, so it ran off the edge instead.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void ALongReportLine_Wraps_AndKeepsOpenFolderInside_AtTheNarrowestWindow()
    {
        var failures = new List<string>();

        StaViewHarness.OnSta(() =>
        {
            var view = new BulkCutView
            {
                DataContext = new BulkCutViewModel(
                    new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                    new FakeSettings(), new FakeBulkTrimEngine()),
            };
            var (w, h) = StaViewHarness.Sizes.OrderBy(s => s.W).First();
            StaViewHarness.LayOut(view, w, h);

            var summary = StaViewHarness.Find<TextBlock>(view, "CompletedSummary");
            var button = StaViewHarness.Find<Button>(view, "OpenOutputFolderButton");
            if (summary is null || button is null)
            {
                failures.Add("the completed surface's summary or Open folder button was not found");
                return;
            }

            DependencyObject node = summary;
            Border? surface = null;
            while (surface is null && (node = VisualTreeHelper.GetParent(node)) is not null)
            {
                surface = node as Border;
            }

            surface!.Visibility = Visibility.Visible;   // the surface is bound to IsCompleted; force it for the measure
            summary.Text = "Trimmed 12 · Sent 11 to the Recycle Bin. Still in use: The.Show.S01E07.1080p.WEB-DL.mkv "
                + "(held by explorer.exe, MsMpEng.exe) · Kept in the list: 1 original still in use";   // T-185 wording
            StaViewHarness.LayOut(view, w, h);

            Rect Bounds(FrameworkElement e) => e.TransformToAncestor(view).TransformBounds(new Rect(e.RenderSize));
            var edge = Bounds(surface).Right;
            if (Bounds(summary).Right > edge + 0.5)
            {
                failures.Add($"the summary runs past the surface at {w}px ({Bounds(summary).Right:0} > {edge:0})");
            }

            if (Bounds(button).Right > edge + 0.5 || Bounds(button).Right > view.ActualWidth + 0.5)
            {
                failures.Add($"Open folder is pushed out of the surface at {w}px");
            }

            if (summary.ActualHeight < summary.FontSize * 2)
            {
                failures.Add("the long summary did not wrap onto a second line");
            }
        });

        failures.Should().BeEmpty();
    }

    /// <summary>T-185 — the tooltip says what the box does now, exactly: the list empties, the originals stay on disk
    /// where ✕ Delete originals no longer reaches them, and what still stays.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheCheckboxTooltip_SaysExactlyWhatHappens()
    {
        string? tooltip = null;
        StaViewHarness.OnSta(() =>
        {
            var view = new BulkCutView
            {
                DataContext = new BulkCutViewModel(
                    new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                    new FakeSettings(), new FakeBulkTrimEngine()),
            };
            StaViewHarness.LayOut(view, 1280, 800);
            tooltip = StaViewHarness.Descendants<CheckBox>(view)
                .Single(c => (c.Content as string) == "Auto-clear list").ToolTip as string;
        });

        tooltip.Should().Be(
            "After a batch finishes with no failures, take the videos it trimmed out of the list and keep its summary "
            + "on screen. If you keep your originals, they stay on disk beside the outputs, where ✕ Delete originals can "
            + "no longer reach them — tick Auto-delete originals to have them binned, or leave this off to review first. "
            + "Videos not in this run stay in the list, and so does any original Auto-delete could not bin.");
    }

    private static DependencyProperty ToggleButton_IsChecked => System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;
}
