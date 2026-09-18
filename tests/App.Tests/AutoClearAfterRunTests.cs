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
/// <para><b>What it never takes away</b> (G-055 criterion 3, T-171 decision (b) as refined after review): a row
/// that was not part of the run (unticked, no cut yet, added while the batch ran), a row whose original is still the
/// user's to delete (✕ Delete originals needs it), and a row the run reported a warning on (the row is the only
/// place that warning is shown). The summary says how many were kept and why.</para>
/// </summary>
public sealed class AutoClearAfterRunTests
{
    private const string ExactFellBack = "exact cut unavailable (no encoder for this codec) - cut snapped to the nearest keyframe";

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

    /// <summary>A clean batch that wrote a NEW file beside each original — the originals are left to delete.</summary>
    private static void NewFileOutputs(FakeBulkTrimEngine engine, params string[] outputs)
        => engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            items.Select((item, i) => new BulkTrimItemResult(item, ItemOutcome.Done, outputs[i], null, Array.Empty<string>()))
                .ToList());

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
    /// Decision (b): auto-delete is off and the batch wrote new files, so every original is still the user's to delete
    /// — and ✕ Delete originals needs the rows. The rows stay and the summary says why. This is also the case that
    /// rules out option (a), "clear anyway".
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OriginalsLeftToDelete_KeepTheirRows_AndTheSummarySaysWhy()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("b.mp4"));
        NewFileOutputs(engine, MakeVideo("a_trimmed.mp4"), MakeVideo("b_trimmed.mp4"));
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().HaveCount(2, "clearing would take away the only way to delete those originals");
        vm.CanDeleteOriginals.Should().BeTrue("the manual Delete originals button is still there to press");
        vm.Operation.ResultSummary.Should().StartWith("Trimmed 2", "the run's own line comes first");
        vm.Operation.ResultSummary.Should().Contain("2 originals").And.Contain("Delete originals",
            "the user is told why the rows stayed and what to do about them");
    }

    /// <summary>
    /// Auto-delete armed, one original binned and one refused (still in use). The binned row goes; the refused row's
    /// original is still on disk for the user to deal with, so that row — and only that row — stays.
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
            .And.Contain("1 original left to delete", "and the reason the row stayed is added to it");
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
        vm.Operation.ResultSummary.Should().Contain("2 not in this run");
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
    }

    /// <summary>
    /// A row the run reported a warning on — here an exact cut that fell back to a keyframe and was written over the
    /// original — keeps its row: the row's Warning is the only place that warning is shown, and the batch still counts
    /// as clean.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowTheRunWarnedAbout_StaysInTheList_AndTheSummarySaysSo()
    {
        var (vm, probe, engine, _) = Build(new RecordingDisposer());
        var warned = await AddRowAsync(vm, probe, MakeVideo("warned.mp4"));
        await AddRowAsync(vm, probe, MakeVideo("clean.mp4"));
        ReplaceMode(vm);
        ReplacedInPlace(engine, i => i == 0 ? new[] { ExactFellBack } : Array.Empty<string>());
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.BatchState.Should().Be(BulkBatchState.Completed, "precondition: a warning does not make the batch unclean");
        vm.Items.Should().Equal(new[] { warned }, "the warned row stays; the clean one goes");
        warned.Warning.Should().Contain("exact cut unavailable", "and its warning is still on it");
        vm.Operation.ResultSummary.Should().Contain("1 row with a warning");
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
                + "(held by explorer.exe, MsMpEng.exe) · Kept in the list: 1 original left to delete (✕ Delete originals)";
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

    private static DependencyProperty ToggleButton_IsChecked => System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;
}
