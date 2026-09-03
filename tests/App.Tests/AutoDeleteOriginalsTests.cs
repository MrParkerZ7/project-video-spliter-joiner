using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Io;
using System.Collections.Generic;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-156 (SPEC-011) — auto-delete originals after a successful batch, and the optional empty-bin step.
///
/// <para>Requested because the disk fills mid-session: <i>"sometime space remaining too low and require
/// to be clear"</i>. Reclaiming space was two manual steps after every batch.</para>
///
/// <para><b>What is actually being tested here is the safety, not the feature.</b> Auto-delete plus
/// auto-empty is <b>permanent deletion with no undo</b>, and everything that makes deleting originals
/// safe to offer today rests on the file still being in the bin afterwards. The interesting cases are
/// therefore all the ones where it must NOT fire: a partly-failed batch, an unwired confirmation, the
/// bin box armed on its own, and the delete box being switched back off.</para>
/// </summary>
public sealed class AutoDeleteOriginalsTests
{
    private static (BulkCutViewModel Vm, FakeSettings Settings) Build()
    {
        var settings = new FakeSettings();
        var vm = new BulkCutViewModel(
            new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
            settings, new FakeBulkTrimEngine());
        return (vm, settings);
    }

    /// <summary>
    /// Records what was handed to it and actually removes the file, mirroring the real disposer's
    /// best-effort contract. Local to this suite: the equivalent in DeleteOriginalsTests is private.
    /// </summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        public void DisposeOriginalBackup(string backupPath)
        {
            Disposed.Add(backupPath);
            try { File.Delete(backupPath); } catch { /* mirrors the real disposer */ }
        }
    }

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "vsj-t156-" + Guid.NewGuid().ToString("N"));

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "original content");
        return p;
    }

    private async Task<BulkItemViewModel> AddRowAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 2);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(10);
        return row;
    }

    // ---- The gate that protects data: only a CLEAN batch ---------------------------------------------

    /// <summary>
    /// T-156 — a partly-failed batch must never auto-delete.
    ///
    /// <para>This is the whole safety of the feature. A run where some rows failed is precisely when the
    /// originals are still the only good copy, and the user is not present to notice — they armed this in
    /// advance. Deleting there would destroy the source of a trim that did not happen.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task APartlyFailedBatchNeverAutoDeletes()
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();

        // A REAL disposer. Without one DeleteOriginals() returns immediately and this test passes no
        // matter what the guard does — which is exactly how it first slipped through: the mutation
        // "remove the clean-batch guard" survived, because the sweep was inert either way.
        var disposer = new RecordingDisposer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, engine,
            originalDisposer: disposer);

        var good = await AddRowAsync(vm, probe, MakeVideo("good.mp4"));
        var bad = await AddRowAsync(vm, probe, MakeVideo("bad.mp4"));

        var goodOut = MakeVideo("good_trimmed.mp4");
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.CompletedWithFailures,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, goodOut, null, Array.Empty<string>()),
                new(items[1], ItemOutcome.Failed, null,
                    new UserFacingError(ErrorCategory.Unknown, "boom", "tail"), Array.Empty<string>()),
            });

        vm.AutoDeleteOriginals = true;

        await vm.RunBatchAsync();

        disposer.Disposed.Should().BeEmpty(
            "one row failed — the whole batch is suspect, and the originals are the only good copy left");
        File.Exists(good.Path).Should().BeTrue("not even the row that succeeded");
        File.Exists(bad.Path).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ACleanBatchAutoDeletes_WithoutAskingAgain()
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var disposer = new RecordingDisposer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, engine,
            originalDisposer: disposer);

        var row = await AddRowAsync(vm, probe, MakeVideo("clean.mp4"));
        var output = MakeVideo("clean_trimmed.mp4");

        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, output, null, Array.Empty<string>()),
            });

        var asked = 0;
        vm.ConfirmDeleteOriginals = (_, _) => { asked++; return true; };
        vm.AutoDeleteOriginals = true;

        await vm.RunBatchAsync();

        disposer.Disposed.Should().Contain(row.Path, "the batch was clean and the user armed this");
        asked.Should().Be(0, "the checkbox IS the consent — asking again on every batch defeats the point");
        vm.Operation.ResultSummary.Should().Contain(
            "Recycle Bin", "it still reports what it did, exactly as the manual button does");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task NothingHappensWhenTheOptionIsOff()
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var disposer = new RecordingDisposer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, engine,
            originalDisposer: disposer);

        var row = await AddRowAsync(vm, probe, MakeVideo("kept.mp4"));
        var output = MakeVideo("kept_trimmed.mp4");
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, output, null, Array.Empty<string>()),
            });

        await vm.RunBatchAsync();

        disposer.Disposed.Should().BeEmpty("the default is off, and off must mean nothing happens");
        File.Exists(row.Path).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheBinIsOnlyEmptiedWhenBothAreArmed()
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, engine,
            originalDisposer: new RecordingDisposer());

        await AddRowAsync(vm, probe, MakeVideo("x.mp4"));
        var output = MakeVideo("x_trimmed.mp4");
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            new List<BulkTrimItemResult>
            {
                new(items[0], ItemOutcome.Done, output, null, Array.Empty<string>()),
            });

        var emptied = 0;
        vm.EmptyRecycleBinAction = () => emptied++;
        vm.AutoDeleteOriginals = true;   // bin box left OFF

        await vm.RunBatchAsync();

        emptied.Should().Be(0, "binning is recoverable; emptying is not, and it was never armed");
    }

    // ---- Defaults: destructive things are off until asked for ---------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void BothOptionsDefaultOff()
    {
        var (vm, _) = Build();

        vm.AutoDeleteOriginals.Should().BeFalse("a destructive default is not a default");
        vm.AutoEmptyRecycleBin.Should().BeFalse();
        vm.DestructiveOutputNote.Should().BeNull("nothing is armed, so there is nothing to warn about");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheChoicePersists()
    {
        var (vm, settings) = Build();

        vm.AutoDeleteOriginals = true;

        settings.BulkAutoDeleteOriginals.Should().BeTrue("it is a preference, not a per-session toggle");
    }

    // ---- The bin box cannot be armed casually --------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheBinBoxIsMeaninglessUntilAutoDeleteIsOn()
    {
        var (vm, _) = Build();

        vm.CanAutoEmptyRecycleBin.Should().BeFalse();

        vm.ConfirmPermanentDeletion = () => true;
        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeFalse(
            "emptying the bin without auto-delete frees nothing this app put there");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AnUnwiredHostCannotArmPermanentDeletion()
    {
        var (vm, _) = Build();
        vm.AutoDeleteOriginals = true;

        // ConfirmPermanentDeletion deliberately NOT wired — the default must refuse.
        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeFalse(
            "the default answer to 'may I delete their files permanently?' is no");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void DecliningTheConfirmationLeavesItOff()
    {
        var (vm, _) = Build();
        vm.AutoDeleteOriginals = true;
        vm.ConfirmPermanentDeletion = () => false;

        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AcceptingTheConfirmationArmsIt_AndTheFooterSaysPermanent()
    {
        var (vm, _) = Build();
        vm.AutoDeleteOriginals = true;
        vm.ConfirmPermanentDeletion = () => true;

        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeTrue();
        vm.DestructiveOutputNote.Should().Contain(
            "PERMANENT", "the state must be visible BEFORE Run is pressed, not discovered afterwards");
        vm.DestructiveOutputNote.Should().Contain("not recoverable");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TurningAutoDeleteOffDisarmsTheBinBoxToo()
    {
        var (vm, _) = Build();
        vm.AutoDeleteOriginals = true;
        vm.ConfirmPermanentDeletion = () => true;
        vm.AutoEmptyRecycleBin = true;

        vm.AutoDeleteOriginals = false;

        vm.AutoEmptyRecycleBin.Should().BeFalse(
            "otherwise re-ticking auto-delete later would silently re-arm permanent deletion without " +
            "asking again — consent given once for a state that was since dismantled");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheConfirmationIsAskedOnce_NotOnEveryBatch()
    {
        var (vm, _) = Build();
        var asked = 0;
        vm.AutoDeleteOriginals = true;
        vm.ConfirmPermanentDeletion = () => { asked++; return true; };

        vm.AutoEmptyRecycleBin = true;
        vm.AutoEmptyRecycleBin = true;   // already on — no state change, no question

        asked.Should().Be(1, "a dialog on every batch is the interruption T-155 just removed");
    }

    // ---- The footer note -----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AutoDeleteAloneSaysRecycleBin_NotPermanent()
    {
        var (vm, _) = Build();
        vm.AutoDeleteOriginals = true;

        vm.DestructiveOutputNote.Should().Contain("Recycle Bin");
        vm.DestructiveOutputNote.Should().NotContain(
            "PERMANENT", "binning IS recoverable — overstating it would train people to ignore the note");
    }
}
