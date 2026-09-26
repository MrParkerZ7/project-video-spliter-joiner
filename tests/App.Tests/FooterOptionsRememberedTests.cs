using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using VideoSplitJoiner.Core.Bulk;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-186 (from note T-184, "check-box on button feature isn't fully yet remember latest stage") — Bulk Cut's footer
/// options remember their last state. Three already did (Auto-clear list, Auto-delete originals, and empty bin);
/// <b>Overwrite existing output</b>, <b>Exact cut</b> and <b>Replace originals</b> reset to off on every launch. Replace
/// keeps its per-batch confirmation; Overwrite, which has neither a confirmation nor a Recycle Bin, says so in the
/// footer's red destructive note while it is on.
/// </summary>
public sealed class FooterOptionsRememberedTests : IDisposable
{
    private const string OverwriteClause = "Overwrite existing output is on — existing _trimmed files are replaced in place, not recoverable";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t186-" + Guid.NewGuid().ToString("N"));

    public enum Option
    {
        Overwrite,
        ExactCut,
        ReplaceOriginal,
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    // ---- remembered -------------------------------------------------------------------------------------

    /// <summary>Repro (fails on the shipped code): toggled on, then a new view-model over the same settings finds it on.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(Option.Overwrite)]
    [InlineData(Option.ExactCut)]
    [InlineData(Option.ReplaceOriginal)]
    public void AnOptionTickedOnce_IsStillTickedNextLaunch(Option option)
    {
        var settings = new FakeSettings();
        var first = Build(settings);

        Set(first, option, true);
        Get(Build(settings), option).Should().BeTrue("the choice is remembered across launches");

        Set(first, option, false);
        Get(Build(settings), option).Should().BeFalse("and so is turning it off");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void WithNothingStored_AllThreeStartOff()
    {
        var vm = Build(new FakeSettings());

        (vm.Overwrite, vm.ExactCut, vm.ReplaceOriginal).Should().Be((false, false, false),
            "an older settings file keeps today's defaults");
    }

    /// <summary>Restoring is startup STATE, not a gesture: building the view-model writes nothing back.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void RestoringTheOptions_WritesNothingBack()
    {
        var settings = new FakeSettings { BulkOverwrite = true, BulkExactCut = true, BulkReplaceOriginals = true };
        var before = settings.FooterOptionWrites;

        var vm = Build(settings);

        (vm.Overwrite, vm.ExactCut, vm.ReplaceOriginal).Should().Be((true, true, true), "precondition: restored");
        settings.FooterOptionWrites.Should().Be(before, "a restore is not a user gesture");
    }

    // ---- the run uses them ------------------------------------------------------------------------------

    /// <summary>
    /// The notes are not the point — the batch is. The restored values reach <see cref="BulkTrimOptions"/>, so a note
    /// can never say ON while the run uses the old value (the T-162 shape).
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RestoredReplaceAndExact_ReachTheBatch()
    {
        var engine = new FakeBulkTrimEngine();
        var (vm, probe) = BuildWithEngine(new FakeSettings { BulkReplaceOriginals = true, BulkExactCut = true }, engine);
        vm.ConfirmReplaceOriginals = _ => true;
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));

        await vm.RunBatchAsync();

        engine.ReceivedOptions.Should().NotBeNull("precondition: the batch ran");
        engine.ReceivedOptions!.Output.Should().Be(OutputMode.ReplaceOriginal);
        engine.ReceivedOptions.Precision.Should().Be(CutPrecision.Exact);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RestoredOverwrite_ReachesTheBatch()
    {
        var engine = new FakeBulkTrimEngine();
        var (vm, probe) = BuildWithEngine(new FakeSettings { BulkOverwrite = true }, engine);
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));

        await vm.RunBatchAsync();

        engine.ReceivedOptions!.Collision.Should().Be(CollisionPolicy.Overwrite);
    }

    /// <summary>Remembering Replace removes no safeguard: every batch still asks, and a refusal still blocks it.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RestoredReplace_StillAsksBeforeEveryBatch_AndSaysSoBeforeRun()
    {
        var engine = new FakeBulkTrimEngine();
        var (vm, probe) = BuildWithEngine(new FakeSettings { BulkReplaceOriginals = true }, engine);
        await AddRowAsync(vm, probe, MakeVideo("a.mp4"));
        var asked = 0;
        vm.ConfirmReplaceOriginals = _ => { asked++; return false; };

        vm.CollisionIsInert.Should().BeTrue();
        vm.OutputNote.Should().StartWith("Output → REPLACES each original file", "the footer says it before any run");

        await vm.RunBatchAsync();

        asked.Should().Be(1, "the per-batch confirmation still runs");
        engine.CallCount.Should().Be(0, "and refusing it still blocks the batch");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RestoredExactCut_SaysSo_AndANewRowCutsExactly()
    {
        var (vm, probe) = BuildWithEngine(new FakeSettings { BulkExactCut = true }, new FakeBulkTrimEngine());

        vm.PrecisionNote.Should().NotStartWith("Lossless", "the precision note reflects the restored choice");

        var row = await AddRowAsync(vm, probe, MakeVideo("a.mp4"), introSeconds: 11);
        row.IntroEnd.SuppressSnapNote.Should().BeTrue("a row added later inherits the remembered Exact cut");
    }

    // ---- Overwrite says so, in red ----------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void Overwrite_ShowsItsRedNote_WhileReplaceIsOff()
    {
        var vm = Build(new FakeSettings());
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.OverwriteOutputNote.Should().BeNull("precondition: Overwrite is off");

        vm.Overwrite = true;
        vm.OverwriteOutputNote.Should().Be(OverwriteClause);
        raised.Should().Contain(nameof(BulkCutViewModel.OverwriteOutputNote), "the footer must update when it is ticked");

        raised.Clear();
        vm.ReplaceOriginal = true;
        vm.OverwriteOutputNote.Should().BeNull("Replace makes the collision policy inert, so the sentence would be false");
        raised.Should().Contain(nameof(BulkCutViewModel.OverwriteOutputNote));

        vm.ReplaceOriginal = false;
        vm.OverwriteOutputNote.Should().Be(OverwriteClause);

        raised.Clear();
        vm.Overwrite = false;
        vm.OverwriteOutputNote.Should().BeNull();
        raised.Should().Contain(nameof(BulkCutViewModel.OverwriteOutputNote), "and when it is unticked");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void RestoredOverwrite_IsInRedFromTheFirstFrame_BesideAnyAutoDeleteNote()
    {
        Build(new FakeSettings { BulkOverwrite = true }).OverwriteOutputNote.Should().Be(OverwriteClause);

        var both = Build(new FakeSettings { BulkOverwrite = true, BulkAutoDeleteOriginals = true });
        both.OverwriteOutputNote.Should().Be(OverwriteClause);
        both.DestructiveOutputNote.Should().Be(
            "Originals will be sent to the Recycle Bin after each successful batch", "each warning is its own element");
    }

    /// <summary>
    /// The overwrite warning is its own red, SemiBold note closing the irreversible group after the delete note
    /// (SPEC-011 I137/I146), both are actually on screen with their text, and the longest footer they make still wraps
    /// inside the narrowest window, above Run.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheLongestFooter_IsRed_AndStillWrapsAboveRun_AtTheNarrowestWindow()
    {
        var failures = new List<string>();

        StaViewHarness.OnSta(() =>
        {
            var settings = new FakeSettings
            {
                BulkOverwrite = true,
                BulkExactCut = true,
                BulkAutoDeleteOriginals = true,
                BulkAutoEmptyRecycleBin = true,
                BulkAutoClearAfterRun = true,
            };
            var view = new BulkCutView { DataContext = Build(settings) };
            var (w, h) = StaViewHarness.Sizes.OrderBy(s => s.W).First();
            StaViewHarness.LayOut(view, w, h);

            var note = StaViewHarness.Find<TextBlock>(view, "OverwriteOutputNote");
            var deleteNote = StaViewHarness.Find<TextBlock>(view, "DestructiveOutputNote");
            if (note is null || deleteNote is null)
            {
                failures.Add("a red note is missing from the view");
                return;
            }

            // Found by name is not shown: a mistyped binding would leave a note collapsed and still pass the edge check.
            foreach (var (shown, text) in new[]
            {
                (note, OverwriteClause),
                (deleteNote, "Originals will be deleted PERMANENTLY after each successful batch — not recoverable"),
            })
            {
                if (shown.Visibility != Visibility.Visible || shown.ActualWidth <= 0 || shown.Text != text)
                {
                    failures.Add($"'{text}' is not shown (visibility {shown.Visibility}, width {shown.ActualWidth:0}, text '{shown.Text}')");
                }
            }

            if (note.FontWeight != FontWeights.SemiBold)
            {
                failures.Add($"the note is {note.FontWeight}, not SemiBold");
            }

            var danger = ((SolidColorBrush)view.FindResource("DangerBrush")).Color;
            if (note.Foreground is not SolidColorBrush { Color: var c } || c != danger)
            {
                failures.Add("the note is not in the danger colour");
            }

            var options = StaViewHarness.Find<FrameworkElement>(view, "FooterOptions")!;
            var run = StaViewHarness.Find<FrameworkElement>(view, "RunBatchButton")!;
            // Every option and every note in the row — the red ones included — must end inside the window.
            foreach (var element in StaViewHarness.Descendants<FrameworkElement>(options)
                         .Where(e => e is CheckBox || (e is TextBlock t && ReferenceEquals(VisualTreeHelper.GetParent(t), options))))
            {
                var right = element.TranslatePoint(new Point(0, 0), view).X + element.ActualWidth;
                if (right > w + 1)
                {
                    failures.Add($"{element.GetType().Name} ends at x={right:0}, past the {w}px window");
                }
            }

            var optionsBottom = options.TranslatePoint(new Point(0, 0), view).Y + options.ActualHeight;
            if (optionsBottom > run.TranslatePoint(new Point(0, 0), view).Y + 1)
            {
                failures.Add("the options run into Run's line");
            }
        });

        failures.Should().BeEmpty();
    }

    // ---- plumbing ---------------------------------------------------------------------------------------

    private static BulkCutViewModel Build(FakeSettings settings) =>
        new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe) BuildWithEngine(FakeSettings settings, FakeBulkTrimEngine engine)
    {
        var probe = new BulkFakeProbe();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, engine,
            originalDisposer: new NoDisposer());
        return (vm, probe);
    }

    private static bool Get(BulkCutViewModel vm, Option option) => option switch
    {
        Option.Overwrite => vm.Overwrite,
        Option.ExactCut => vm.ExactCut,
        _ => vm.ReplaceOriginal,
    };

    private static void Set(BulkCutViewModel vm, Option option, bool value)
    {
        switch (option)
        {
            case Option.Overwrite: vm.Overwrite = value; break;
            case Option.ExactCut: vm.ExactCut = value; break;
            default: vm.ReplaceOriginal = value; break;
        }
    }

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "video bytes");
        return path;
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

    private sealed class NoDisposer : VideoSplitJoiner.Core.Io.IOriginalDisposer
    {
        public void DisposeOriginalBackup(string backupPath)
        {
        }
    }
}
