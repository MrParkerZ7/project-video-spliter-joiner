using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-163 (G-052, SPEC-010) — auto-delete the split source, with the opt-in empty-bin.
///
/// <para>The literal request: <i>"on Split feature also add auto remove original and auto clear bin same
/// as bulk cut feature"</i>. The reason is the one [[T-156]] recorded and it bites harder here: a split
/// MULTIPLIES disk usage, so reclaiming by hand after every run is the chore this removes.</para>
///
/// <para><b>The safety, not the feature, is what these tests are about.</b> Auto-delete plus auto-empty
/// is permanent deletion with no undo, firing without a per-run gesture. So the interesting cases are
/// all the ones where it must NOT happen — an incomplete set of parts, an unwired confirmation, the bin
/// box armed on its own, and auto-delete being switched back off after both were on.</para>
/// </summary>
public sealed class SplitAutoDeleteTests : IDisposable
{
    private readonly string _dir;

    public SplitAutoDeleteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t163-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Make(string name, int bytes = 2048)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Asked { get; } = new();

        public void DisposeOriginalBackup(string backupPath)
        {
            Asked.Add(backupPath);
            try { File.Delete(backupPath); } catch { /* mirrors the best-effort contract */ }
        }
    }

    private sealed class ScriptedSplitEngine : ISplitEngine
    {
        private readonly IReadOnlyList<string> _outputs;

        public ScriptedSplitEngine(IReadOnlyList<string> outputs) => _outputs = outputs;

        public Task<SplitResult> SplitAsync(
            SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default,
            IProgress<OperationStatus>? status = null, IProgress<PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(
                _outputs.Select(p => new SplitSegment(p, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero)).ToList(),
                Array.Empty<string>()));
    }

    /// <summary>Build a VM ready to split, WITHOUT running it — so a test can arm flags first.</summary>
    private (SplitViewModel Vm, RecordingDisposer Disposer, FakeSettings Settings, string Source, string[] Parts)
        Ready(int partCount = 2, string[]? parts = null)
    {
        var source = Make("source.mp4", 4 * 1024 * 1024);
        parts ??= Enumerable.Range(1, partCount).Select(i => Make($"part{i}.mp4")).ToArray();

        var disposer = new RecordingDisposer();
        var settings = new FakeSettings();
        var vm = new SplitViewModel(
            new BulkFakeProbe(), new ScriptedSplitEngine(parts), player: null, settings,
            originalDisposer: disposer);

        return (vm, disposer, settings, source, parts);
    }

    private static async Task RunSplit(SplitViewModel vm, string source, string outDir)
    {
        await vm.LoadAsync(source);
        vm.AddCutAtCommand.Execute(TimeSpan.FromSeconds(10));
        vm.OutputDir = outDir;
        await vm.RunSplitAsync();
    }

    // ---- Defaults + persistence ---------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void BothFlagsDefaultOff_AndPersist()
    {
        var (vm, _, settings, _, _) = Ready();

        vm.AutoDeleteSource.Should().BeFalse("a destructive default is not a default");
        vm.AutoEmptyRecycleBin.Should().BeFalse();

        vm.AutoDeleteSource = true;
        settings.SplitAutoDeleteSource.Should().BeTrue("the choice has to survive a restart");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void SplitFlagsAreSeparateFromBulkCuts()
    {
        // Arming one screen must not silently arm the other — they are different files at different
        // moments, and the user consented to one of them.
        var (vm, _, settings, _, _) = Ready();

        vm.AutoDeleteSource = true;

        settings.BulkAutoDeleteOriginals.Should().NotBe(true, "Bulk Cut's own flag is untouched");
    }

    // ---- The four arming gates ----------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TheBinBoxCannotBeArmedOnItsOwn()
    {
        var (vm, _, _, _, _) = Ready();
        vm.ConfirmPermanentDeletion = () => true;   // even with consent available

        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeFalse(
            "emptying the bin frees nothing this app put there unless auto-delete is also on");
        vm.CanAutoEmptyRecycleBin.Should().BeFalse("and the checkbox is disabled to say so");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void ArmingTheBinBoxRequiresConfirmation_AndAnUnwiredHostCannotArmIt()
    {
        var (vm, _, _, _, _) = Ready();
        vm.AutoDeleteSource = true;

        // ConfirmPermanentDeletion deliberately NOT wired — the default must refuse.
        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeFalse(
            "a host that forgot to wire the prompt must not be able to arm permanent deletion");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void DecliningTheConfirmationLeavesTheBinBoxOff()
    {
        var (vm, _, _, _, _) = Ready();
        vm.AutoDeleteSource = true;
        vm.ConfirmPermanentDeletion = () => false;

        vm.AutoEmptyRecycleBin = true;

        vm.AutoEmptyRecycleBin.Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TurningAutoDeleteOffDisarmsTheBinBox()
    {
        // The subtle one. Without this, tick-both → untick-first → re-tick-first would silently re-arm
        // PERMANENT deletion on consent given for a configuration that no longer existed.
        var (vm, _, _, _, _) = Ready();
        var asked = 0;
        vm.ConfirmPermanentDeletion = () => { asked++; return true; };

        vm.AutoDeleteSource = true;
        vm.AutoEmptyRecycleBin = true;
        vm.AutoEmptyRecycleBin.Should().BeTrue("precondition — both armed");

        vm.AutoDeleteSource = false;
        vm.AutoEmptyRecycleBin.Should().BeFalse("the bin box disarms with it");

        // Re-arming must ask AGAIN rather than inherit the earlier yes.
        vm.AutoDeleteSource = true;
        vm.AutoEmptyRecycleBin.Should().BeFalse("re-ticking auto-delete does not re-arm the bin");
        vm.AutoEmptyRecycleBin = true;
        asked.Should().Be(2, "consent is asked per arming, never inherited");
    }

    // ---- What the footer says BEFORE Run ------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TheNoteSaysRecycleBinWhenItIsRecoverable_AndPermanentOnlyWhenItIsNot()
    {
        var (vm, _, _, _, _) = Ready();
        vm.ConfirmPermanentDeletion = () => true;

        vm.DestructiveOutputNote.Should().BeNull("nothing destructive is armed");

        vm.AutoDeleteSource = true;
        vm.DestructiveOutputNote.Should().Contain("Recycle Bin");
        vm.DestructiveOutputNote.Should().NotContain(
            "PERMANENT", "binning IS recoverable, and overstating it teaches people to ignore the warning");

        vm.AutoEmptyRecycleBin = true;
        vm.DestructiveOutputNote.Should().Contain("PERMANENT", "now it really is unrecoverable");
    }

    // ---- Firing, and refusing to fire ---------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task ACleanSplitWithAutoDeleteArmed_BinsTheSourceWithNoFurtherGesture()
    {
        var (vm, disposer, _, source, _) = Ready();
        vm.AutoDeleteSource = true;

        await RunSplit(vm, source, _dir);

        disposer.Asked.Should().ContainSingle().Which.Should().Be(source);
        File.Exists(source).Should().BeFalse("the checkbox WAS the consent");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task WithTheFlagOff_ACleanSplitDeletesNothing()
    {
        var (vm, disposer, _, source, _) = Ready();

        await RunSplit(vm, source, _dir);

        disposer.Asked.Should().BeEmpty();
        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AnIncompleteSetOfPartsNeverAutoDeletes()
    {
        // The whole point of routing through DeleteOriginal: the all-parts rule still decides, and the
        // skipped confirmation does not become a skipped SAFETY check.
        //
        // Note the disposer is REAL and wired here. T-156's equivalent test was vacuous precisely
        // because its VM had none, so the sweep was inert and the assertion proved nothing.
        var missing = Path.Combine(_dir, "never-written.mp4");
        var (vm, disposer, _, source, _) = Ready(parts: new[] { Make("p1.mp4"), missing });
        vm.AutoDeleteSource = true;

        await RunSplit(vm, source, _dir);

        disposer.Asked.Should().BeEmpty("one part never reached disk — the source is the only copy");
        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AZeroBytePartNeverAutoDeletes()
    {
        var empty = Make("p2.mp4", bytes: 0);
        var (vm, disposer, _, source, _) = Ready(parts: new[] { Make("p1.mp4"), empty });
        vm.AutoDeleteSource = true;

        await RunSplit(vm, source, _dir);

        disposer.Asked.Should().BeEmpty("present-but-empty is still missing footage");
        File.Exists(source).Should().BeTrue();
    }

    // ---- Emptying the bin ----------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task TheBinIsEmptiedOnlyWhenArmed_AndOnlyAfterTheSourceActuallyWent()
    {
        var (vm, _, _, source, _) = Ready();
        var emptied = 0;
        vm.EmptyRecycleBinAction = () => emptied++;
        vm.ConfirmPermanentDeletion = () => true;
        vm.AutoDeleteSource = true;
        vm.AutoEmptyRecycleBin = true;

        await RunSplit(vm, source, _dir);

        File.Exists(source).Should().BeFalse();
        emptied.Should().Be(1);
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task ARefusedDeleteNeverEmptiesTheBin()
    {
        // Emptying after a refused delete would destroy unrelated files for no gain whatsoever — the
        // bin is not scoped to this app.
        var missing = Path.Combine(_dir, "never-written.mp4");
        var (vm, _, _, source, _) = Ready(parts: new[] { Make("p1.mp4"), missing });
        var emptied = 0;
        vm.EmptyRecycleBinAction = () => emptied++;
        vm.ConfirmPermanentDeletion = () => true;
        vm.AutoDeleteSource = true;
        vm.AutoEmptyRecycleBin = true;

        await RunSplit(vm, source, _dir);

        File.Exists(source).Should().BeTrue("the delete was refused");
        emptied.Should().Be(0, "so there is nothing of ours in the bin to free, and other files would pay");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AutoDeleteWithoutTheBinFlagLeavesTheBinAlone()
    {
        var (vm, _, _, source, _) = Ready();
        var emptied = 0;
        vm.EmptyRecycleBinAction = () => emptied++;
        vm.AutoDeleteSource = true;

        await RunSplit(vm, source, _dir);

        File.Exists(source).Should().BeFalse("auto-delete fired");
        emptied.Should().Be(0, "but the bin was never armed — the source stays restorable");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TheBinActionDefaultsToDoingNothing()
    {
        // A default that empties the developer's Recycle Bin during a test run would be its own bug.
        var (vm, _, _, _, _) = Ready();
        var act = () => vm.EmptyRecycleBinAction();
        act.Should().NotThrow();
    }
}
